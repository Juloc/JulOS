using System.Net;
using System.Text;
using JulOS.Backend.Modules;

namespace JulOS.Backend.Tests;

/// <summary>Fakes fuer die Module Caddy und DDNS: nichts davon fasst ein echtes System an.</summary>
internal sealed class FakeCaddyAdmin(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.Method, request.RequestUri!.ToString()));
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Antwortet mit einer festen Antwort und merkt sich die Anfrage (fuer Router und Lookup-Dienst).</summary>
internal sealed class FakeWeb(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    }
}

internal sealed class FakeResolver(Dictionary<string, string[]> names) : IHostResolver
{
    public int Calls;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (!names.TryGetValue(host, out var list)) throw new System.Net.Sockets.SocketException(11001);
        return Task.FromResult(list.Select(IPAddress.Parse).ToArray());
    }
}

internal sealed class FakeIp(string? address) : IPublicIpSource
{
    public string Name => "Fake";
    public int Calls;

    public Task<IPAddress> GetAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return address is null ? throw new HttpRequestException("down") : Task.FromResult(IPAddress.Parse(address));
    }
}

internal sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Ein Fake-Dienst als Handler: antwortet je Adresse (Host, Pfad und Query) mit Status und Text, merkt sich jede Anfrage samt Kopfzeilen.</summary>
internal sealed class FakeServiceWeb(Dictionary<string, (HttpStatusCode Status, string Body)> routes) : HttpMessageHandler
{
    public List<(string Url, Dictionary<string, string> Headers)> Requests { get; } = [];
    public Exception? Throw { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Requests) Requests.Add((request.RequestUri!.ToString(), headers));
        if (Throw is not null) throw Throw;
        if (!routes.TryGetValue(request.RequestUri!.ToString(), out var r)) r = (HttpStatusCode.NotFound, "");
        return Task.FromResult(new HttpResponseMessage(r.Status) { Content = new StringContent(r.Body, Encoding.UTF8, "application/json") });
    }
}
