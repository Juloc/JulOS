using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Tests;

/// <summary>Ein Agent im Speicher: antwortet auf einem Unix-Socket wie tools/julos-agent.py (Agent-API v1) und merkt sich die Anfragen.</summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string SocketPath { get; } = Path.Combine(Path.GetTempPath(), $"julos-test-{Guid.NewGuid():N}.sock");
    public List<(string Method, string Path, string Body)> Requests { get; } = [];
    /// <summary>Ersetzt die Antwort fuer einen Pfad (Status, Body) oder bremst sie (Verzoegerung).</summary>
    public Dictionary<string, (int Status, string Body)> Overrides { get; } = [];
    public TimeSpan Delay { get; set; }

    public FakeAgent()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.ListenUnixSocket(SocketPath));
        _app = b.Build();
        _app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            lock (Requests) Requests.Add((ctx.Request.Method, ctx.Request.Path.Value!, body));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay);
            var (status, text) = Overrides.TryGetValue(ctx.Request.Path.Value!, out var o) ? o : DefaultReply(ctx.Request.Method, ctx.Request.Path.Value!);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(text);
        });
        _app.Start();
    }

    private static (int, string) DefaultReply(string method, string path) => (method, path) switch
    {
        ("GET", "/v1/health") => (200, """{"ok":true,"api":1,"platform":"linux","mode":"desktop"}"""),
        ("GET", "/v1/windows") => (200, """{"api":1,"mode":"desktop","screen":{"w":856,"h":1448},"windows":[{"id":12345,"title":"Claude","app":"claude","class":"Claude","pid":99,"minimized":false,"maximized":true,"active":true,"icon":""}]}"""),
        ("GET", "/v1/apps") => (200, """{"apps":[{"id":"claude.desktop","name":"Claude","icon":""}]}"""),
        ("POST", _) => (200, """{"ok":true}"""),
        _ => (404, """{"error":"unbekannt"}"""),
    };

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        try { File.Delete(SocketPath); } catch (IOException) { }
    }
}
