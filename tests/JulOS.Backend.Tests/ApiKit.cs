using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Tests;

/// <summary>Gemeinsame Hilfen fuer die HTTP-Tests der Ordner, Einstellungen und von Quick Connect (echte Middleware und Datenbank).</summary>
internal static class ApiKit
{
    public const string Password = "korrektes pferd batterie";
    public const string SetupCode = "TEST-CODE-0000";

    internal static async Task<Guid> Insert(TestDb db, string sql, params object?[] args) => await db.ScalarAsync<Guid>(sql, args);
    internal static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal sealed class Server(TestDb db, string? allowed = null, List<string>? log = null, string? secretKey = null) : WebApplicationFactory<Program>
    {
        public HttpClient Http() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("JULOS_DB", db.ConnectionString);
            builder.UseSetting("JULOS_SETUP_CODE", SetupCode);
            if (allowed is not null) builder.UseSetting("JULOS_ALLOWED_HOSTS", allowed);
            if (secretKey is not null) builder.UseSetting("JULOS_SECRET_KEY", secretKey);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, LoopbackFilter>());
            if (log is not null) builder.ConfigureTestServices(s => s.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new Capture(log))));
        }
    }

    internal sealed class Capture(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(lines);
        public void Dispose() { }
        internal sealed class Sink(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { lock (lines) lines.Add(formatter(state, exception) + exception); }
        }
    }

    internal sealed class LoopbackFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback; return n(ctx); });
            next(app);
        };
    }

    internal static HttpRequestMessage Req(HttpMethod method, string url, string? cookie, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (method != HttpMethod.Get) { req.Headers.Add("X-JulOS", "1"); req.Headers.Add("Origin", "http://localhost"); }
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    internal static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    internal static async Task<(string Cookie, Guid UserId)> SignedInAsync(HttpClient c, TestDb db)
    {
        var begin = await c.SendAsync(Req(HttpMethod.Post, "/api/setup/begin", null, new { code = SetupCode, user = "jul", password = Password }));
        var secret = (await Json(begin)).GetProperty("secret").GetString()!;
        var totp = Totp.Hotp(Base32.Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var finish = await c.SendAsync(Req(HttpMethod.Post, "/api/setup/finish", null, new { code = SetupCode, user = "jul", totp }));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
        return (finish.Headers.GetValues("Set-Cookie").First().Split(';')[0], await db.ScalarAsync<Guid>("select id from users limit 1"));
    }
}
