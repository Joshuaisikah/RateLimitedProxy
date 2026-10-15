using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace RateLimitedProxy.Tests;

// A real HTTP server on a random local port that the proxy forwards to.
//
//   /status/{code}  -> responds with that status, body "status {code}", header X-Upstream: fake
//   /hang           -> never responds (until the caller gives up)
//   /stall-body     -> sends 200 + headers immediately, then never sends the body
//   /slow/{ms}      -> waits that many milliseconds, then 200
//   /advance/{ms}   -> moves Clock (a FakeTimeProvider) forward by ms, then 200 - an exact
//                      latency as seen by a proxy that reads time from that clock
//   anything else   -> 200 with a JSON echo of what the upstream actually received
public sealed class FakeUpstream : IAsyncLifetime
{
    private WebApplication? _app;

    public string BaseUrl { get; private set; } = "";

    // Set by tests that hand the same FakeTimeProvider to the proxy; used by /advance/{ms}.
    public FakeTimeProvider? Clock { get; set; }

    // How many requests reached the upstream. Lets tests prove the proxy really called it.
    private int _hits;
    public int Hits => Volatile.Read(ref _hits);

    // How many requests are inside the upstream right now, and the most there have ever been
    // at once. This is what proves a concurrency cap: the proxy can't fake it from its side.
    private int _inFlight;
    private int _peakInFlight;
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);
    public bool InFlightIsZero => Volatile.Read(ref _inFlight) == 0;
    public void ResetPeak() => Volatile.Write(ref _peakInFlight, Volatile.Read(ref _inFlight));

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.Use(async (ctx, next) =>
        {
            Interlocked.Increment(ref _hits);
            var now = Interlocked.Increment(ref _inFlight);
            int peak;
            while (now > (peak = Volatile.Read(ref _peakInFlight))
                   && Interlocked.CompareExchange(ref _peakInFlight, now, peak) != peak) { }
            try { await next(); }
            finally { Interlocked.Decrement(ref _inFlight); }
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

        _app.MapGet("/slow/{ms:int}", async (int ms, HttpContext ctx) =>
        {
            await Task.Delay(ms, ctx.RequestAborted);
            return Results.Text($"slow {ms}");
        });

        _app.MapGet("/advance/{ms:int}", (int ms) =>
        {
            if (Clock is null) return Results.Text("no clock set", statusCode: 500);
            Clock.Advance(TimeSpan.FromMilliseconds(ms));
            return Results.Text($"advanced {ms}");
        });

        // Sends status + headers straight away, then never sends the body.
        _app.Map("/stall-body", async (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.StartAsync(ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
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
