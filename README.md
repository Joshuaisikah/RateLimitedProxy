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
| 6 | Circuit breaker (5 failures → fail fast 30 s → half-open) | ✅ 136/136 tests |
| 7 | p50 / p95 latency on the metrics endpoint | ✅ 182/182 tests |

## Contract

| Setting / route | Meaning |
|---|---|
| `Upstream:BaseUrl` | Where requests are forwarded to. If it has a path, the path must end in `/` (e.g. `https://host/v1/`), or the proxy won't start |
| `Upstream:TimeoutMs` | Deadline for the whole request, **including the wait for a slot** |
| `Upstream:MaxConcurrency` | Most requests inside the upstream at once (default `20`) |
| `RateLimit:ApiKeys` | The API keys allowed to use the proxy. Required: no default, no blanks, no duplicates. Case-sensitive |
| `RateLimit:Capacity` | Burst size, and the most tokens a bucket can hold (default `10`, at least `1`) |
| `RateLimit:RefillPerSecond` | Tokens added back per second, fractions kept (default `1.0`, finite and > 0) |
| `CircuitBreaker:FailureThreshold` | Consecutive upstream failures that open the circuit (default `5`, at least `1`) |
| `CircuitBreaker:BreakDurationSeconds` | How long an open circuit fails fast before letting one trial through (default `30`, at least `1`) |
| `X-Api-Key` header | The client's key. Header name is case-insensitive, the value is not. Never forwarded upstream |
| `ANY /proxy/{**path}` | Forwards method, path, query, headers and body to the upstream |
| `GET /metrics` | Counters, error rate and p50/p95 latency as JSON. No key, no token, never forwarded, never counts itself |

## What the client gets back

| Situation | Client sees | Counted in metrics? |
|---|---|---|
| No `X-Api-Key`, or a key not in the list | `401 Unauthorized` + `WWW-Authenticate: ApiKey realm="RateLimitedProxy"`, upstream never called | Only in `TotalUnauthorized` |
| Key's bucket is empty | `429 Too Many Requests` + `Retry-After: <seconds>`, upstream never called | Only in `TotalRateLimited` |
| Circuit open, or half-open with the trial already in flight | `503 Service Unavailable` + `X-Proxy-Reason: circuit-open` + `Retry-After`, upstream never called | Only in `TotalCircuitOpen` |
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

### Circuit breaker (M6)

When the upstream keeps failing, stop sending it traffic for a while instead of making every
client wait for a timeout. One breaker for the whole proxy: it protects the upstream, and a
per-key breaker would let a dead upstream be hit once per key.

```
request ──▶ key check ──▶ rate check ──▶ breaker ──▶ concurrency slot ──▶ upstream
             │ unknown       │ empty       │ open /      │ no slot by deadline
             ▼               ▼             ▼ trial busy  ▼
            401             429           503 +        503
                                          X-Proxy-Reason: circuit-open
```

```
            5 consecutive failures
  CLOSED ───────────────────────────▶ OPEN ◀──────────────┐
    ▲                                  │                  │ trial fails
    │                                  │ a request arrives│ (opened-at = now)
    │ trial succeeds                   │ ≥ 30 s later     │
    │                                  ▼                  │
    └──────────────────────────────  HALF-OPEN ───────────┘
                                 one trial goes upstream,
                                 everyone else gets the open-circuit 503
```

- **Transitions are lazy.** Nothing fires at the 30-second mark. The first request after it
  checks the clock and moves the state on, the same way the token bucket refills when asked.
  No timer, no background thread. Time comes from the injected `TimeProvider`, so the tests
  can jump 30 s in one call.
- **What counts as a failure:** the upstream answered any `5xx`, the proxy's `504`
  (including a request that waited for a slot and then ran out of time), and the proxy's
  `502` (unreachable). Anything else the upstream answers, `4xx` included, is a **success**,
  because the upstream is alive and answering. A success resets the count, so
  F F F F S F is one consecutive failure, not five.
- **What doesn't count, and doesn't reset either:** client aborts, `401`, `429`, cap `503`.
  None of these says anything about the upstream's health.
- **One `lock` around every state change**: `Admit`, `RecordSuccess`, `RecordFailure`,
  `RecordNeutral`. Without it, "is it open? / has 30 s passed? / claim the trial" is
  check-then-act (TOCTOU): 15 requests arriving at t = 30.001 would all see "open, 30 s
  passed" and all become the trial. The lock is **never held during the upstream call**,
  so a hanging trial can't make other requests wait just to be told "rejected". Guarded by
  `Exactly_one_trial_when_15_arrive_at_once`.
- **Every admitted request reports exactly once.** A `reported` flag is set when an outcome
  is recorded, and a `finally` reports *neutral* if nothing else did. That handles the trial
  that never gets a verdict: its client aborts, or it loses the slot race and gets a cap
  `503`. Without that, the trial's claim would never be released and the breaker would stay
  half-open forever, rejecting everything. Guarded by
  `A_trial_whose_client_aborts_releases_the_claim`.
- **The outcome is recorded before the body is copied.** By then the upstream has answered.
  A client hanging up during the copy can't turn a real answer into "neutral".
- **Only the trial moves the circuit out of half-open.** Requests admitted while closed can
  come back late, after the circuit has opened. Their results are ignored unless the
  circuit is closed. Otherwise a slow success from before the outage could close it, or a
  burst of late failures could reopen it and push the cooldown back. Guarded by
  `A_late_success_while_open_is_ignored` and
  `Late_failures_while_open_do_not_restart_the_cooldown`.
- **A failed trial restarts the cooldown from the trial**, not from the first opening.
- **The breaker check runs after the key and rate checks.** An unknown key gets `401` even
  while the circuit is open, so strangers learn nothing about the upstream's health. A
  circuit-rejected request has spent its token: *token = attempt*, the same rule as for
  the cap `503`.
- **The two `503`s can be told apart.** A circuit `503` carries
  `X-Proxy-Reason: circuit-open` and is counted in `TotalCircuitOpen`. A cap `503` has
  neither. "Proxy full" and "upstream dead" lead to different fixes.
- **`Retry-After`.** When open, it's the seconds left until half-open, rounded up and never
  `0` (same reasoning as M5). When half-open it's `1`, because the trial resolves within
  `TimeoutMs`.

### Latency percentiles and `/metrics` (M7)

```json
{
  "totalRequests": 4, "totalErrors": 1, "errorRate": 0.25,
  "totalRejected": 0, "totalRateLimited": 0, "totalUnauthorized": 0, "totalCircuitOpen": 0,
  "sampleCount": 4, "p50Ms": 50, "p95Ms": 100, "overflowCount": 0
}
```

- **What latency means:** from the request arriving at the proxy to the response being
  decided, read from the injected `TimeProvider`. That's what the client experiences,
  slot wait included.
- **Which requests are sampled:** every *admitted* request (one that passed the key, rate and
  breaker checks) except client aborts. Upstream answers of any status, `504`s (about
  `TimeoutMs`), `502`s and cap `503`s are all sampled. A request that crashed with an
  unhandled exception is still sampled: it was admitted and the client didn't leave.
  `401`, `429` and circuit `503`s are not sampled. Those take about 0 ms and would drag p50
  towards zero during an outage, so the number would say "faster" at the worst moment.
  Skipping cap `503`s and queue `504`s would do the opposite: they hide exactly the
  requests that show the proxy is overloaded.
- **`sampleCount` ≠ `totalRequests`.** Cap `503`s are sampled but, since M4, are not in
  `totalRequests`.
- **Storage: fixed buckets since startup.** Upper edges in ms, inclusive (≤): 1, 2, 5, 10,
  25, 50, 100, 250, 500, 1000, 2500, 5000, plus an overflow bucket. Memory stays constant
  however many requests arrive, and recording is one `Interlocked.Increment`. The cost is
  that the exact value is lost: only the bucket is known.
- **Percentile = nearest rank, reported as the bucket's upper edge.** rank = ⌈p × N⌉
  (`(pct * n + 99) / 100` in integers). Walk the buckets from the fastest, keeping a running
  total, and stop at the first bucket whose total reaches the rank. Reporting the upper edge
  never under-reports, the same choice as rounding `Retry-After` up. With samples
  5, 10, …, 100 ms, p95 is rank 19 = 95 ms, reported as `100`.
- **Exponential edges.** 3 ms vs 13 ms matters, 4003 ms vs 4013 ms doesn't, so the buckets
  are narrow where latencies are small and wide where they're big.
- **No data vs too slow.** With 0 samples both percentiles are `null` (rank ⌈p × 0⌉ = 0
  would otherwise "find" the first bucket and report 1 ms with no data at all). When the
  rank lands in the overflow bucket, that percentile is also `null`. `sampleCount` and
  `overflowCount` tell the two apart.
- **`errorRate` = `totalErrors` ÷ `totalRequests`, `null` at 0 requests.** Rejections don't
  dilute it. `0` would claim "no errors" for a proxy that has proven nothing.
- **Reading while requests record: two different torn reads.**
  - *Buckets*: the snapshot copies each bucket one at a time, so it matches no exact instant
    (it's *torn*). That's harmless, because N is computed **from the copy**. The walk is
    consistent with the data it walks and always reaches the rank; the worst case is being
    a few microseconds stale. If N came from a separate counter, the walk could run off the
    end.
  - *Counters*: `ProcessRequest` increments requests **before** errors. Reading requests
    and *then* errors lets requests land between the two reads, so the response could say
    3 errors out of 1 request (300%). The endpoint reads **errors first, then requests**,
    once each into locals, and prints those same locals, so errors ≤ requests always holds
    within one response. Tests can't catch this (the gap is nanoseconds); it was found by
    walking a timeline.
- **`/metrics` is outside `/proxy`.** It never reaches the proxy handler, so it's never
  forwarded, never rate limited, and never counts itself.

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
- **Queue-induced `504`s feed the breaker (decided in M6, worth revisiting).** With a shared
  deadline, a waiter often gets a slot with a few ms left and then times out as a `504`.
  M6 counts those as failures because the breaker can't tell why a request was slow. The
  cost: under load, a healthy but slow upstream plus a long queue can trip the circuit.
  A fix would give the upstream call its own minimum budget, or skip the call when too
  little time is left.
- **Half-open `Retry-After: 1` vs a 5 s trial.** The trial can take up to `TimeoutMs`
  (5 s by default). A client that obeys `1` may be rejected again several times. Would
  `ceil(TimeoutMs / 1000)` be more honest?
- **One trial means slow recovery.** After an outage, a single success closes the circuit
  and the full load returns at once. Gradual ramp-up (several trials, or a percentage of
  traffic) is how bigger systems avoid knocking a just-recovered upstream over again.
- **`X-Proxy-Reason` is a made-up header.** RFC 9457 *problem details*
  (`application/problem+json` with a `type`) would be the standard way to say why.

- **Keys need a restart.** Buckets are built once at startup. Picking up new keys without a
  restart means rebuilding the dictionary safely while requests are reading it
  (`IOptionsMonitor`, swap in a new immutable dictionary). Would `FrozenDictionary` say
  "read-only after startup" more clearly than `Dictionary`?
- **Two `X-Api-Key` headers.** The first value is used. Should more than one key in a single
  request be a `401`?
- **Should `429`s refund nothing, ever?** Token = attempt was chosen for simplicity. Fairer
  alternatives (refund on `503`) need the refund to be atomic with refill and capped at
  `Capacity`.

- **`/metrics` is public on the proxy's only port.** Anyone who can reach the proxy can see
  traffic volume and error rate, and could time an attack for when errors spike. The usual
  fix is a separate *management port* (Kestrel listening on two ports, the endpoint
  restricted with `RequireHost("*:port")`).
- **Since startup, not a window.** After a day of traffic, a 5-minute incident barely moves
  p95. Options: reset on scrape, a ring of per-minute histograms, or exporting the raw
  buckets to Prometheus and letting it compute rates over a window.
- **Bucket edges are fixed, `TimeoutMs` isn't.** With `TimeoutMs` = 5000, every timeout
  lands just above 5000 ms, in overflow, so p95 goes `null` the moment more than 5% of
  requests time out. Should the edges be derived from config?

## Running

```
dotnet test
```

The tests start a real fake upstream on a random local port (`FakeUpstream`) and run the
proxy in-process with `WebApplicationFactory`.
