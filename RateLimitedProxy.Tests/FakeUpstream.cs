using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RateLimitedProxy.Tests;

// A real HTTP server on a random local port that the proxy forwards to.
//
//   /status/{code}  -> responds with that status, body "status {code}", header X-Upstream: fake
//   /hang           -> never responds (until the caller gives up)
//   anything else   -> 200 with a JSON echo of what the upstream actually received
public sealed class FakeUpstream : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";

    // How many requests reached the upstream. Lets tests prove the proxy really called it.
    private int _hits;
    public int Hits => Volatile.Read(ref _hits);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.Use(async (ctx, next) =>
        {
            Interlocked.Increment(ref _hits);
            await next();
        });

        _app.MapGet("/status/{code:int}", (int code, HttpContext ctx) =>
        {
            ctx.Response.Headers["X-Upstream"] = "fake";
            return Results.Text($"status {code}", statusCode: code);
        });

        _app.Map("/hang", async (HttpContext ctx) =>
        {
            try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
            catch (OperationCanceledException) { }
        });

        _app.Map("/{**rest}", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();

            var echo = new EchoResponse(
                ctx.Request.Method,
                ctx.Request.Path.Value ?? "",
                ctx.Request.QueryString.Value ?? "",
                ctx.Request.Host.Value ?? "",
                ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body);

            return Results.Json(echo);
        });

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses;
        BaseUrl = addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

public sealed record EchoResponse(
    string Method,
    string Path,
    string Query,
    string Host,
    Dictionary<string, string> Headers,
    string Body)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
