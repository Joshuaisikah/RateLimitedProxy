using Microsoft.Extensions.Options;
using RateLimitedProxy.Api.Services;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<UpstreamOptions>(builder.Configuration.GetSection("Upstream"));
builder.Services.AddHttpClient<Forwarder>(
    (sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<UpstreamOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
        client.Timeout = Timeout.InfiniteTimeSpan; // we handle timeouts ourselves
    }
);
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

// Map controllers when present (no-op for pure minimal APIs)
app.Map(
    "/proxy/{**path}",
    async (string? path, HttpContext ctx, Forwarder forwarder, Metrics metrics) =>
    {
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

            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (HopByHopHeaders.IsHopByHop(header.Key))
                    continue;
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
            }

            await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (UpstreamTimeoutException)
        {
            ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            metrics.ProcessRequest(StatusCodes.Status504GatewayTimeout);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected. Nobody to answer, and not an upstream error.
        }
        catch (HttpRequestException) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            metrics.ProcessRequest(StatusCodes.Status502BadGateway);
        }
    }
);
app.Run();
