using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MightyClaude.Core;

public sealed class HttpHost : IAsyncDisposable
{
    private readonly WebApplication app;
    public Uri Address { get; }
    private HttpHost(WebApplication app, Uri address) { this.app = app; Address = address; }
    public static async Task<HttpHost> StartAsync(IPAddress address, int port, RequestDelegate handler, CancellationToken token = default, long maximumBody = 512 * 1024)
    {
        // This embedded server is configured only by the app. Default builders
        // read/watch appsettings in the caller's CWD, which may be a huge user
        // checkout and must not influence a loopback or Tailscale listener.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(server => { server.Listen(address, port); server.Limits.MaxRequestBodySize = maximumBody; server.Limits.MaxConcurrentConnections = 64; server.Limits.MaxRequestHeadersTotalSize = 8192; server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(1); });
        var app = builder.Build(); app.Run(handler);
        try { await app.StartAsync(token); var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(); return new(app, new(url)); }
        catch { await app.DisposeAsync(); throw; }
    }
    public static async Task<JsonDocument> ReadJsonAsync(HttpContext context, int maximum = 512 * 1024, int timeoutSeconds = 10)
    {
        if (context.Request.ContentType?.Split(';')[0].Trim() != "application/json" || context.Request.ContentLength > maximum) throw new InvalidDataException("JSON 요청 크기 또는 형식이 올바르지 않습니다.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, timeout.Token)) > 0) { if (data.Length + count > maximum) throw new InvalidDataException("요청 크기 제한을 초과했습니다."); data.Write(buffer, 0, count); }
        return JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
    public static async Task ReplyAsync(HttpContext context, int status, object value)
    {
        context.Response.StatusCode = status; context.Response.ContentType = "application/json; charset=utf-8"; context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Connection = "close"; context.Response.Headers[RemoteNetwork.VersionHeader] = "1";
        await context.Response.WriteAsync(JsonSerializer.Serialize(value, Wire.Json), context.RequestAborted);
    }
    public async ValueTask DisposeAsync() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); try { await app.StopAsync(timeout.Token); } catch (OperationCanceledException) { } await app.DisposeAsync(); }
}
