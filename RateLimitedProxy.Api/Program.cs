using Microsoft.Extensions.Options;
using RateLimitedProxy.Api.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder
    .Services.AddOptions<UpstreamOptions>()
    .Bind(builder.Configuration.GetSection(UpstreamOptions.SectionName))
    .Validate(o => o.BaseUrl != string.Empty, "BaseUrl must be set")
    .Validate(o => o.TimeoutMs > 0, "TimeoutMs must be greater than 0")
    .Validate(o => o.MaxConcurrency > 0, "MaxConcurrency must be greater than 0")
    .Validate(
        o =>
            Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "BaseUrl must be a valid HTTP or HTTPS URL"
    )
    .Validate(
        o =>
            Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.EndsWith('/'),
        "BaseUrl must end with '/' when it has a path, e.g. https://host/v1/"
    )
    .ValidateOnStart();
builder.Services.AddHttpClient<Forwarder>(
    (sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<UpstreamOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = Timeout.InfiniteTimeSpan; // we handle timeouts ourselves
    }
);
builder
    .Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName))
    .Validate(o => o.Capacity >= 1, "RateLimit:Capacity must be at least 1")
    .Validate(
        o => double.IsFinite(o.RefillPerSecond) && o.RefillPerSecond > 0,
        "RateLimit:RefillPerSecond must be a positive number"
    )
    .Validate(
        o => o.ApiKeys.Length > 0 && o.ApiKeys.All(k => !string.IsNullOrWhiteSpace(k)),
        "RateLimit:ApiKeys must contain at least one non-empty key"
    )
    .Validate(
        o => o.ApiKeys.Distinct(StringComparer.Ordinal).Count() == o.ApiKeys.Length,
        "RateLimit:ApiKeys must not contain duplicates"
    )
    .ValidateOnStart();
builder
    .Services.AddOptions<CircuitBreakerOptions>()
    .Bind(builder.Configuration.GetSection(CircuitBreakerOptions.SectionName))
    .Validate(o => o.FailureThreshold >= 1, "CircuitBreaker:FailureThreshold must be at least 1")
    .Validate(
        o => o.BreakDurationSeconds >= 1,
        "CircuitBreaker:BreakDurationSeconds must be at least 1"
    )
    .ValidateOnStart();
builder.Services.AddSingleton<CircuitBreaker>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<ConcurrencyLimiter>();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<Metrics>();

builder.Services.AddControllers();

var app = builder.Build();

// Development-only: serve the OpenAPI document + Scalar UI
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // → /openapi/v1.json
    app.MapScalarApiReference(); // → /scalar
}

app.UseHttpsRedirection();
app.Map(
    "/proxy/{**path}",
    async (
        string? path,
        HttpContext ctx,
        Forwarder forwarder,
        Metrics metrics,
        RateLimiter limiter,
        CircuitBreaker breaker // new
    ) =>
    {
        var apikey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();

        var rate = limiter.Check(apikey);

        switch (rate.Outcome)
        {
            case RateLimitOutcome.UnknownKey:
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers["WWW-Authenticate"] = "ApiKey realm=\"RateLimitedProxy\"";
                metrics.RecordUnauthorized();
                return;
            case RateLimitOutcome.Limited:
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.Response.Headers["Retry-After"] = rate.RetryAfterSeconds.ToString();
                metrics.RecordRateLimited();
                return;
            case RateLimitOutcome.Allowed:
                break;
            default:
                throw new InvalidOperationException(
                    $"Unexpected rate limit outcome: {rate.Outcome}"
                );
        }

        // new: may this request go through? (auth -> rate -> breaker -> slot)
        var admission = breaker.Admit();
        if (admission.Outcome == AdmitOutCome.Rejected)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers["X-Proxy-Reason"] = "circuit-open";
            ctx.Response.Headers["Retry-After"] = admission.RetryAfterSeconds.ToString();
            metrics.RecordCircuitOpen();
            return;
        }
        var isTrial = admission.Outcome == AdmitOutCome.Trial;
        var reported = false;

        try
        {
            var hasBody =
                ctx.Request.ContentLength > 0
                || ctx.Request.Headers.ContainsKey("Transfer-Encoding");

            using var response = await forwarder.ForwardRequestAsync(
                ctx.Request.Method,
                path ?? "",
                ctx.Request.QueryString.Value ?? "",
                ctx.Request.Headers,
                hasBody ? ctx.Request.Body : null,
                ctx.RequestAborted
            );

            var status = (int)response.StatusCode;
            ctx.Response.StatusCode = status;
            metrics.ProcessRequest(status);

            // new: report the upstream's answer BEFORE copying the body,
            // so a client hanging up during the copy can't make it neutral
            if (status >= 500)
                breaker.RecordFailure(isTrial);
            else
                breaker.RecordSuccess(isTrial);
            reported = true;

            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (HeadersNotForwarded.ShouldSkip(header.Key))
                    continue;
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            }

            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (ProxyBusyException)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            metrics.RecordRejected();
            // neutral: the finally releases a trial claim
        }
        catch (UpstreamTimeoutException)
        {
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            metrics.ProcessRequest(StatusCodes.Status504GatewayTimeout);
            breaker.RecordFailure(isTrial); // new
            reported = true; // new
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected. Neutral: the finally releases a trial claim.
        }
        catch (HttpRequestException) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            metrics.ProcessRequest(StatusCodes.Status502BadGateway);
            breaker.RecordFailure(isTrial); // new
            reported = true; // new
        }
        finally // new
        {
            if (!reported)
                breaker.RecordNeutral(isTrial);
        }
    }
);
app.Run();
