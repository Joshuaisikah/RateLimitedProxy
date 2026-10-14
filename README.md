# RateLimitedProxy

A proxy in front of a third-party API that enforces limits and protects the upstream.
Project 07 of the .NET ladder (see `~/dotnet/PROJECTS.md`).

## Milestones

| # | Milestone | Status |
|---|---|---|
| 1 | Skeleton | ✅ |
| 2 | Metrics counters (`Interlocked`, exact under concurrency) | ✅ |
| 3 | Forwarding + timeout | ✅ 55/55 tests |
| 4 | Global cap on concurrent upstream calls (`SemaphoreSlim`) | ✅ 62/62 tests |
| 5 | Per-API-key token bucket → `429` + `Retry-After` | ✅ 98/98 tests |
| 6 | Circuit breaker (5 failures → fail fast 30 s → half-open) | next |
| 7 | p50 / p95 latency on the metrics endpoint | |

## Contract

| Setting / route | Meaning |
|---|---|
| `Upstream:BaseUrl` | Where requests are forwarded to. If it has a path, the path must end in `/` (e.g. `https://host/v1/`), or the proxy won't start |
| `Upstream:TimeoutMs` | Deadline for the whole request, **including the wait for a slot** |
| `Upstream:MaxConcurrency` | Most requests inside the upstream at once (default `20`) |
| `RateLimit:ApiKeys` | The API keys allowed to use the proxy. Required: no default, no blanks, no duplicates. Case-sensitive |
| `RateLimit:Capacity` | Burst size, and the most tokens a bucket can hold (default `10`, at least `1`) |
| `RateLimit:RefillPerSecond` | Tokens added back per second, fractions kept (default `1.0`, finite and > 0) |
| `X-Api-Key` header | The client's key. Header name is case-insensitive, the value is not. Never forwarded upstream |
| `ANY /proxy/{**path}` | Forwards method, path, query, headers and body to the upstream |

## What the client gets back

| Situation | Client sees | Counted in metrics? |
|---|---|---|
| No `X-Api-Key`, or a key not in the list | `401 Unauthorized` + `WWW-Authenticate: ApiKey realm="RateLimitedProxy"`, upstream never called | Only in `TotalUnauthorized` |
| Key's bucket is empty | `429 Too Many Requests` + `Retry-After: <seconds>`, upstream never called | Only in `TotalRateLimited` |
| Upstream answers (any status) | That status, body and headers, unchanged | Yes, as that status |
| No slot free before the deadline | `503 Service Unavailable`, upstream never called | Only in `TotalRejected`, not a request, not an error |
| Upstream doesn't finish within `TimeoutMs` | `504 Gateway Timeout` | Yes, as an error |
| Upstream unreachable (refused, DNS, reset) | `502 Bad Gateway` | Yes, as an error |
| Client disconnects first | Nothing, nobody is listening | No (same idea as nginx's `499`) |

A timeout and a client abort both show up as a cancelled operation. They are told apart by
asking whose token fired: the proxy's own deadline, or `HttpContext.RequestAborted`.

## Design decisions

### Headers

- `Host` is not forwarded. The upstream gets its own host, otherwise name-based virtual
  hosting on the upstream breaks (wrong site or `421 Misdirected Request`).
- Hop-by-hop headers (RFC 9110 §7.6.1) are dropped in both directions, including any header
  named inside `Connection`. `Proxy-Authorization` is a credential for this proxy, not for
  the upstream. The same goes for `X-Api-Key`: forwarding it would put every client's key in
  the upstream's logs.
- The set is called `HeadersNotForwarded`, not `HopByHopHeaders`: `Host` and `X-Api-Key`
  aren't hop-by-hop. The name says *why* they're skipped, not which RFC category they're in.
- `Content-Type` and the other body headers describe the body, so in .NET they live on the
  content, not on the request.

### Buffering, not streaming

The proxy reads the **whole** upstream response before it sends anything to the client.

```
BUFFERING (chosen)
upstream ──[hdrs][chunk][chunk][end]──▶ proxy memory
                                            │
client   ◀───────────────────────────── [hdrs + whole body]

STREAMING (rejected for now)
upstream ──[hdrs]──[chunk]──[chunk]──[end]
              ▼       ▼        ▼       ▼
client   ◀─[hdrs]──[chunk]──[chunk]──[end]
```

Why buffering fits this project:

- **One deadline covers everything.** `TimeoutMs` covers headers *and* body. With streaming,
  the body is read after the forwarder returns, outside the deadline. Only
  `RequestAborted`, which the **client** controls, can stop that wait. An upstream that sends
  headers and then stalls would hold the proxy until the client gives up. That happened
  in this project and is guarded by `Upstream_that_stalls_after_headers_is_cut_off_by_the_timeout`.
- **Honest status codes.** The status is only sent after the whole upstream answer is in.
  If the upstream dies halfway, the client can still get a real `502`/`504`. With streaming,
  `200` has already gone out and the only remaining signal is to cut the connection.
- **Honest metrics.** The recorded status is decided with the full upstream answer in
  hand. With streaming, an upstream that dies mid-body would already be counted as a success.
- **Frees the upstream from slow clients.** The upstream connection is done as soon as the
  body is in memory. The proxy, not the upstream, serves the slow client. nginx buffers by
  default for this reason (`proxy_buffering`).

What it costs, and when to revisit:

- **Memory:** body size × concurrent requests. Large downloads would need a size limit
  (see `HttpClient.MaxResponseContentBufferSize`) or a hybrid design.
- **Time to first byte:** the client waits for the full upstream transfer.
- **Never-ending responses break:** Server-Sent Events, long-polling and token streams never
  "finish", so a buffering proxy returns `504` for a healthy upstream.
- Streaming would need a choice between a **total** timeout and an **idle** timeout
  (time between chunks), e.g. nginx `proxy_read_timeout`, YARP `ActivityTimeout`.

Lesson: switching to `HttpCompletionOption.ResponseHeadersRead` looked like a performance
tweak, but it changed a correctness property. Changing *when* an awaited call returns
changes what its cancellation token protects.

### Concurrency cap (M4)

At most `MaxConcurrency` requests are inside the upstream at once. The cap protects the
**upstream**. When it's full, a request waits for a slot, but only until its own `TimeoutMs`
deadline. There's one budget for the whole request, so the deadline starts *before* the wait.

```
t0      deadline starts
t0..tS  wait for a slot     ── deadline passes here → 503, upstream never called
tS..tE  call the upstream   ── deadline passes here → 504
tE      slot released (body is already in proxy memory, thanks to buffering)
```

- **A cap `503` is not an error.** It's the proxy shedding load (*load shedding*), not the
  upstream failing. If it counted as an error, a traffic spike to a healthy upstream would
  trip the circuit breaker (M6). It has its own counter, `TotalRejected`, and isn't in
  `TotalRequests`, which counts requests that got a slot.
- **Acquire before the `try`, release in the `finally`.** `SemaphoreSlim` is just a counter.
  It doesn't track who holds a slot, so a `Release()` from a request that never got one
  raises the cap for good (*over-releasing*). The slot wait sits before the `try`, so a
  wait that throws (client abort, deadline) never reaches the `finally`.
  `new SemaphoreSlim(max, max)` makes an over-release eventually throw `SemaphoreFullException`,
  but only once the count passes the max, and usually on an innocent request's release.
  Guarded by `Slot_wait_timeouts_do_not_raise_the_cap` and
  `Client_aborts_while_waiting_do_not_raise_the_cap`.
- **Releasing the slot before the body is copied to the client is safe only because of
  buffering.** `SendAsync` returns after the whole body is in proxy memory, so the upstream
  is done by then. With `ResponseHeadersRead`, the slot would be released while the
  upstream is still sending, and the cap would quietly stop protecting it.
- **Choosing the number** uses Little's Law: requests in flight = requests per second ×
  average time per request. `20` is a placeholder. The real number comes from what the
  upstream can handle.
- **Bad config fails at startup** (`ValidateOnStart`), not on the first request. A singleton
  is created the first time something asks for it, so without startup validation a
  `MaxConcurrency` of `0` started fine and then served `500` to every request.

### Rate limiting per API key (M5)

Every key gets its own **token bucket**. A request costs one token. The bucket holds at most
`Capacity` tokens (the burst size) and refills at `RefillPerSecond` (the long-run rate).
A bucket starts full, so a new client can burst straight away.

```
request ──▶ key check ──▶ rate check ──▶ concurrency slot ──▶ upstream
             │ unknown       │ empty         │ no slot by deadline
             ▼               ▼               ▼
            401             429             503
```

- **Refill, check and take are one atomic step.** Checking for a token and then taking it in
  two steps is *check-then-act* (TOCTOU): two requests both see the last token and both take
  it. The bucket holds a `lock` around the whole step. It reads the time, adds the tokens
  earned since the last call, then takes one. Guarded by
  `Concurrent_requests_on_one_key_never_spend_more_than_capacity` (20 keys × 40 requests at
  once, exactly 5 get through per key).
- **One lock per bucket, on the bucket's own object.** `alice` and `bob` never wait for each
  other. The lock is never on the key string: `lock` compares *references*, and two `"alice"`
  strings read from two requests' headers are two different objects. (*Reference vs value
  equality*, *string interning*.)
- **Buckets are created at startup, from the key list.** The dictionary is only read after
  that, so a plain `Dictionary` is safe for concurrent reads and there's no `GetOrAdd`
  race when two first-ever requests for a key arrive together. The cost: adding a key needs a
  restart.
- **One lookup answers both questions.** `RateLimiter.Check` returns `UnknownKey`, `Limited`
  or `Allowed`. A separate "is this key known?" list was removed, because two lists can
  disagree, and the endpoint used `?? ""` to quiet the compiler about a null it couldn't
  rule out. The null/blank check runs before `TryGetValue`, which throws on a null key.
- **Only a configured key gets a bucket.** If any string got its own bucket, a client on a
  `429` could just send another made-up key (*key rotation*), and the dictionary would grow
  with every one.
- **Fractions are kept.** Tokens are a `double`. At 1 token per 4 s, asking every 1.5 s gives
  0.375 → 0.75 → 1.125, so the third request passes. A refill that rounded down to whole
  tokens and reset its clock would never pass. Guarded by `Refill_keeps_the_fraction`.
- **Time comes from an injected `TimeProvider`,** read with `GetTimestamp` /
  `GetElapsedTime`. That's a monotonic clock, so an NTP correction to the wall clock can't
  make elapsed time go negative or jump forward. The tests swap in `FakeTimeProvider`, so time
  only moves when a test moves it. (The upstream timeout still runs on real time.)
- **`Retry-After` = seconds until the next whole token, rounded up.** At 1 token per 4 s
  with 0.625 tokens, the next token is 1.5 s away, so the header says `2`. Rounding down
  would say `1`, and a client that obeys it gets another `429`. The trade-off was chosen
  deliberately: a header up to a second too long is better than a client retrying too early.
  There's no rounding *before* the ceiling, because that would bring the too-early error back.
- **The rate check runs before the concurrency slot.** An over-limit client gets its `429`
  straight away and never takes a place in the slot queue from clients within their limit.
- **A token pays for the attempt.** A request that passes the rate check and then gets a cap
  `503` has still spent its token. Refunding would be fairer, but it's a second write to the
  bucket from somewhere else, and it could push the bucket over capacity.
- **Rejections aren't upstream errors.** `401`s and `429`s have their own counters
  (`TotalUnauthorized`, `TotalRateLimited`), stay out of `TotalRequests`, and must never feed
  the circuit breaker (M6). On a dashboard, `TotalRejected` means "the proxy is full" and
  `TotalRateLimited` means "a client is over its limit". Those lead to different actions.
- **Unknown outcomes fail closed.** The endpoint's `switch` handles `Allowed` explicitly and
  throws on anything else, so a future outcome can never fall through to the upstream.
- **Keys aren't in the repo.** In development they come from user secrets. The tests run in a
  `Testing` environment so those keys can't merge into the test config (config arrays merge
  *by index*).

### Base URL with a path

RFC 3986 §5.2.3: when a relative path is resolved against a base URL, the base path's last
segment is dropped. `https://host/v1` + `users/5` → `https://host/users/5`, so `v1` is lost
without any error. The proxy refuses to start if the base URL has a path that doesn't end in
`/`. It checks `Uri.AbsolutePath`, not the raw string, so a host-only URL (path `/`) is fine
with or without the slash.

## Open questions

- **What exactly do the metrics count?** What the upstream answered, what the proxy
  decided, or what the client actually received? With buffering, the status is recorded
  before the body is copied to the client. If the client disconnects mid-copy, the metric
  still says `200`. Whether that's a bug depends on the definition.
- A `GET` with `Content-Type` but no body: the header is silently dropped. Is that fine?
- **What protects the proxy?** The cap protects the upstream, not the proxy. Once a body is
  buffered the slot is freed, but the body stays in memory until the client has read it.
  Take 500 slow clients each downloading 50 MB: none of them holds a slot, the proxy holds
  about 25 GB, and nothing limits it. Options to look at: a response size limit
  (`HttpClient.MaxResponseContentBufferSize`), a separate cap on requests in the proxy as a
  whole, or a client-side timeout for the copy.
- **Waiters turn into `504`s (M6).** With a shared deadline, a single waiter rarely gets a
  `503`. A slot usually frees up just before its deadline, so it gets the slot with a few
  ms left and then times out as a `504`, which counts as an error. When the upstream is
  slow, queueing makes the error count higher than the upstream's real failure rate. Should
  those `504`s feed the circuit breaker?

- **Keys need a restart.** Buckets are built once at startup. Picking up new keys without a
  restart means rebuilding the dictionary safely while requests are reading it
  (`IOptionsMonitor`, swap in a new immutable dictionary). Would `FrozenDictionary` say
  "read-only after startup" more clearly than `Dictionary`?
- **Two `X-Api-Key` headers.** The first value is used. Should more than one key in a single
  request be a `401`?
- **Should `429`s refund nothing, ever?** Token = attempt was chosen for simplicity. Fairer
  alternatives (refund on `503`) need the refund to be atomic with refill and capped at
  `Capacity`.

## Running

```
dotnet test
```

The tests start a real fake upstream on a random local port (`FakeUpstream`) and run the
proxy in-process with `WebApplicationFactory`.
