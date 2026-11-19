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

/// <summary>Ein Fake-DNS-Anbieter: nennt feste Eintraege (oder null = nicht lesbar), merkt sich jedes Schreiben.</summary>
internal sealed class FakeDnsProvider(DnsRecord[]? records, bool ok = true) : IDnsProvider
{
    public string Name => "Fake";
    public List<(string Host, string Address)> Writes { get; } = [];
    public int Reads;

    public Task<IReadOnlyList<DnsRecord>?> ReadAsync(string[] hosts, string type, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<DnsRecord>?>(records?.Where(r => r.Type == type && hosts.Contains(r.Host)).ToArray());
    }

    public Task<DnsWriteResult> UpdateAsync(string host, IPAddress ip, CancellationToken ct)
    {
        Writes.Add((host, ip.ToString()));
        return Task.FromResult(ok ? new DnsWriteResult(true, "ok") : new DnsWriteResult(false, "rejected"));
    }
}

internal sealed class MemoryHistory : IDdnsHistory
{
    public List<DdnsHistoryEntry> Rows { get; } = [];
    public Task AddAsync(DdnsHistoryEntry e, CancellationToken ct) { lock (Rows) Rows.Insert(0, e); return Task.CompletedTask; }
    public Task<IReadOnlyList<DdnsHistoryEntry>> RecentAsync(int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<DdnsHistoryEntry>>([.. Rows.Take(count)]);
    public Task<string?> LastWrittenAsync(string host, string type, CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r => r.Host == host && r.Type == type && r.Ok)?.NewIp);
}

/// <summary>Fake fuer die HTTP-Anbieter: je Methode und Adresse eine Antwort, jede Anfrage samt Kopfzeilen und Koerper wird gemerkt.</summary>
internal sealed class FakeProviderWeb(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
{
    public List<(string Method, string Url, string Body, string? Auth)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        var (status, text) = answer(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
