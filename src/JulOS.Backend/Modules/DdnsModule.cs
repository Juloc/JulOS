using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace JulOS.Backend.Modules;

public sealed record DdnsOptions(ModuleMode Mode, string[] Hosts, Uri? LookupUrl, Uri? RouterUrl, TimeSpan CacheFor)
{
    public const int MaxHosts = 20;
    public static readonly TimeSpan DefaultCache = TimeSpan.FromSeconds(60);

    private static readonly Regex Label = new("^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.Compiled);

    /// <summary>Namen aus "a.example, b.example". Ungueltige Namen und IP-Adressen werden verworfen (und gemeldet), es sind hoechstens <see cref="MaxHosts"/>.</summary>
    public static string[] ParseHosts(string? text, List<string> warnings)
    {
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split([',', ';', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (!ValidName(name)) { warnings.Add("JULOS_DDNS_HOSTS: ein Eintrag ist kein gueltiger Rechnername und wird ignoriert."); continue; }
            if (!list.Contains(name)) list.Add(name);
        }
        if (list.Count > MaxHosts) { warnings.Add($"JULOS_DDNS_HOSTS: nur die ersten {MaxHosts} Namen werden beobachtet."); list.RemoveRange(MaxHosts, list.Count - MaxHosts); }
        return [.. list];
    }

    public static bool ValidName(string name) =>
        name.Length is > 0 and <= 253 && !IPAddress.TryParse(name, out _) && name.Split('.').All(l => Label.IsMatch(l)) && name.Contains('.');
}

public interface IPublicIpSource
{
    string Name { get; }
    Task<IPAddress> GetAsync(CancellationToken ct);
}

public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Die normale Namensaufloesung des Servers (System-Resolver), mit kurzer Frist.</summary>
public sealed class SystemResolver : IHostResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        return await Dns.GetHostAddressesAsync(host, cts.Token);
    }
}

/// <summary>Ein Lookup-Dienst, der die Adresse als reinen Text liefert (GET). Die Adresse kommt aus JULOS_DDNS_IP_URL, es gibt keinen eingebauten Dienst.</summary>
public sealed class UrlIpSource(HttpClient http, Uri url) : IPublicIpSource
{
    public string Name => "Lookup-Dienst";

    public async Task<IPAddress> GetAsync(CancellationToken ct)
    {
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidDataException("Status " + (int)res.StatusCode);
        var text = OutboundHttp.Text(await OutboundHttp.ReadLimitedAsync(res, 512, ct)).Trim();
        return IPAddress.TryParse(text, out var ip) ? ip : throw new InvalidDataException("Keine IP-Adresse.");
    }
}

/// <summary>Fragt den Router im LAN (UPnP, GetExternalIPAddress; die Fritz!Box beantwortet das ohne Anmeldung, wenn "Statusinformationen ueber UPnP" an ist). Nach aussen geht dabei nichts.</summary>
public sealed class RouterIpSource(HttpClient http, Uri baseUrl) : IPublicIpSource
{
    public string Name => "Router";
    public const string ControlPath = "/igdupnp/control/WANIPConn1";
    public const string Service = "urn:schemas-upnp-org:service:WANIPConnection:1";
    private const string Envelope = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:GetExternalIPAddress xmlns:u=\"" + Service + "\"/></s:Body></s:Envelope>";

    public async Task<IPAddress> GetAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, ControlPath)) { Content = new StringContent(Envelope, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{Service}#GetExternalIPAddress\"");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidDataException("Status " + (int)res.StatusCode);
        return ParseResponse(await OutboundHttp.ReadLimitedAsync(res, 16 * 1024, ct));
    }

    public static IPAddress ParseResponse(byte[] xml)
    {
        using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 });
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "NewExternalIPAddress")
                return IPAddress.TryParse(reader.ReadElementContentAsString().Trim(), out var ip) ? ip : throw new InvalidDataException("Keine IP-Adresse.");
        throw new InvalidDataException("Keine Adresse in der Antwort.");
    }
}

/// <summary>State: synced, notSynced, unresolved (Name loest nicht auf), unknown (die oeffentliche IP ist nicht bekannt).</summary>
public sealed record DdnsHostStatus(string Host, string State, string[] Addresses);
/// <summary>State: synced (alle Namen zeigen auf die IP), notSynced (mindestens einer nicht), unknown (keine IP oder keine Namen).</summary>
public sealed record DdnsStatus(string State, string? PublicIp, string? Source, DateTimeOffset CheckedAt, DdnsHostStatus[] Hosts, string? Message);

/// <summary>
/// DDNS, nur lesen und ohne Token: vergleicht die oeffentliche IP (Lookup-Dienst oder Router) mit dem, was die normale Namensaufloesung
/// fuer die eingestellten Namen liefert. Gemessen wird nur auf Anfrage und hoechstens einmal je <see cref="DdnsOptions.CacheFor"/> (kein Hintergrundlauf,
/// kein Abruf nach aussen, solange niemand hinschaut). Es wird nichts geschrieben.
/// </summary>
public sealed class DdnsMonitor(DdnsOptions options, IPublicIpSource? source, IHostResolver resolver, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DdnsStatus? _last;

    public bool Enabled => options.Mode != ModuleMode.Off;
    public string[] Hosts => options.Hosts;

    public async Task<DdnsStatus> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_last is { } last && _time.GetUtcNow() - last.CheckedAt < options.CacheFor) return last;
            return _last = await MeasureAsync(ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<DdnsStatus> MeasureAsync(CancellationToken ct)
    {
        if (options.Hosts.Length == 0)
            return new DdnsStatus("unknown", null, source?.Name, _time.GetUtcNow(), [], "Keine Namen eingestellt (JULOS_DDNS_HOSTS).");

        IPAddress? publicIp = null;
        string? message = null;
        if (source is null) message = "Keine Quelle fuer die oeffentliche IP eingestellt (JULOS_DDNS_FRITZBOX_URL oder JULOS_DDNS_IP_URL).";
        else
        {
            try { publicIp = Normalize(await source.GetAsync(ct)); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException or XmlException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                message = $"Die oeffentliche IP konnte nicht ermittelt werden ({source.Name} antwortet nicht oder unerwartet).";
            }
        }
        if (publicIp is not null && IsPrivate(publicIp))
            message = "Die Quelle meldet eine private Adresse: der Anschluss haengt vermutlich hinter CGNAT oder einem zweiten Router, der Vergleich mit dem DNS sagt dann nichts.";

        var hosts = await Task.WhenAll(options.Hosts.Select(h => ResolveOneAsync(h, publicIp, ct)));
        var state = publicIp is null ? "unknown" : hosts.All(h => h.State == "synced") ? "synced" : "notSynced";
        return new DdnsStatus(state, publicIp?.ToString(), source?.Name, _time.GetUtcNow(), hosts, message);
    }

    private async Task<DdnsHostStatus> ResolveOneAsync(string host, IPAddress? publicIp, CancellationToken ct)
    {
        IPAddress[] found;
        try { found = [.. (await resolver.ResolveAsync(host, ct)).Select(Normalize).Distinct()]; }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return new DdnsHostStatus(host, "unresolved", []); }
        var addresses = found.Select(a => a.ToString()).ToArray();
        if (found.Length == 0) return new DdnsHostStatus(host, "unresolved", []);
        if (publicIp is null) return new DdnsHostStatus(host, "unknown", addresses);
        return new DdnsHostStatus(host, found.Contains(publicIp) ? "synced" : "notSynced", addresses);
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    internal static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254 || b[0] == 0;
    }
}
