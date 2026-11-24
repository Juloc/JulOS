using System.Net;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Modules;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Services;

/// <summary>
/// Wie JulOS an die Adresse einer App schickt. Ein eigener Client: keine Weiterleitungen, kein Proxy, kurze Fristen (OutboundHttp), und die Ziel-Adresse wird beim Verbinden NACH der
/// Namensaufloesung geprueft (<see cref="OidcNet"/>; ein Name, der auf Loopback, Link-Local oder ohne Haken auf das lokale Netz zeigt, wird nie angesprochen). Tests setzen <see cref="Create"/>.
/// </summary>
public sealed class OidcHttp
{
    public Func<OidcNetPolicy, HttpClient> Create { get; set; } = Guarded;

    public static HttpClient Guarded(OidcNetPolicy policy) => OutboundHttp.Create(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var all = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = all.Where(a => OidcNet.Allowed(a, policy)).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException("Ziel nicht erlaubt.");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct); return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    });
}

/// <summary>
/// Abmelden ueberall (OIDC Back-Channel Logout 1.0). Endet eine JulOS-Sitzung, bekommt jede App, die fuer sie ein Token erhielt, ein signiertes Logout-Token an ihre
/// registrierte Adresse (und nur an die). Der Weg: Grants, deren Sitzung nicht mehr gilt, kommen in die Warteschlange (egal, wie die Sitzung endete); jeder Eintrag wird im
/// Hintergrund zugestellt, bei einem Fehler nach 10 s, 1 min, 5 min und 30 min wiederholt (insgesamt fuenf Versuche); das Ergebnis steht im Verlauf der Sicherheit.
/// </summary>
public sealed class OidcLogoutService(OidcFlowStore flow, OidcKeys keys, OidcIssuer issuer, OidcHttp http, SecurityAuditStore audit, TimeProvider clock, ILogger<OidcLogoutService> log)
{
    /// <summary>Wartezeit nach dem n-ten Fehlversuch; danach ist Schluss.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    /// <summary>Fehlen Schluessel oder Aussteller nur zeitweise (falscher Neustart ohne Server-Schluessel), wird nach dieser Zeit erneut versucht, ohne einen Versuch zu zaehlen.</summary>
    public static readonly TimeSpan NotReadyWait = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Ein Durchgang: beendete Sitzungen einreihen, Faelliges zustellen, ab und zu aufraeumen. Gibt die Zahl der versuchten Zustellungen.
    /// Die Apps werden gleichzeitig bedient, innerhalb einer App nacheinander und nur bis zum ersten Fehler: eine tote oder langsame App haelt keine andere auf; was uebrig bleibt, ist beim naechsten Durchgang wieder faellig.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await flow.EnqueueForEndedSessionsAsync(now, ct);
        var due = await flow.DueLogoutsAsync(now, 50, ct);
        await Task.WhenAll(due.GroupBy(j => j.AppId).Select(async group =>
        {
            foreach (var job in group) if (!await DeliverAsync(job, ct)) break;
        }));
        if (now - _lastPurge > TimeSpan.FromHours(1)) { _lastPurge = now; await flow.PurgeAsync(now, ct); await keys.PurgeAsync(ct); }
        return due.Count;
    }

    /// <summary>Stellt einen Eintrag zu. Gibt false, wenn die App nicht erreicht wurde (die uebrigen Eintraege dieser App warten dann).</summary>
    private async Task<bool> DeliverAsync(OidcLogoutJob job, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (job.Uri is null) { await flow.FinishLogoutAsync(job.Id, "no_uri", now, ct); return true; }          // die App hat keine Adresse: es gibt nichts zuzustellen
        if (issuer.Value is null || !keys.Available)
        {
            // nur zeitweise: spaeter noch einmal, nichts geht verloren
            log.LogWarning("OIDC: Logout-Token fuer {App} wartet, es fehlen Aussteller oder Server-Schluessel.", job.AppName);
            await flow.DeferLogoutAsync(job.Id, now + NotReadyWait, "waiting", ct);
            return false;
        }
        // die gespeicherte Adresse gilt nicht mehr (zum Beispiel ist der Aussteller jetzt kein http-localhost mehr): endgueltig, nichts wird gesendet
        if (OidcRules.BackchannelProblem(job.Uri, issuer.Value, job.AllowPrivate) is not null)
        {
            await flow.FinishLogoutAsync(job.Id, "failed: not allowed", now, ct);
            await AuditAsync(job, "oidc.logout.failed", $"{job.AppName}: {job.UserName} (Adresse nicht erlaubt)");
            return true;
        }
        string detail;
        try
        {
            Microsoft.IdentityModel.Tokens.SecurityKey signing;
            try { signing = await keys.ActiveAsync(ct); }
            catch (OidcKeysUnusableException) { await flow.DeferLogoutAsync(job.Id, now + NotReadyWait, "waiting", ct); return false; }          // wie fehlender Schluessel: spaeter, ohne Versuch zu zaehlen
            var token = OidcTokens.CreateLogoutToken(new LogoutTokenClaims(issuer.Value, job.ClientId, job.Sub.ToString(), job.Sid, now, OidcRules.NewJti()), signing);
            using var client = http.Create(OidcNetPolicy.For(job.AllowPrivate, issuer.Value));
            using var body = new FormUrlEncodedContent([new("logout_token", token)]);
            using var request = new HttpRequestMessage(HttpMethod.Post, job.Uri) { Content = body };
            // nur der Kopf der Antwort zaehlt: der Koerper wird nie gelesen (eine App, die endlos Daten schickt, fuellt keinen Speicher), das Freigeben schliesst die Verbindung
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // 200 und 204 sind Erfolg; auch 3xx ist keiner (es wird nichts weitergeleitet)
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                await flow.FinishLogoutAsync(job.Id, "ok", now, ct);
                await AuditAsync(job, "oidc.logout.ok", $"{job.AppName}: {job.UserName} ({job.Reason})");
                return true;
            }
            detail = "http " + (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { detail = "timeout"; }
        catch (HttpRequestException e) { detail = "unreachable: " + e.Message; }
        catch (Exception e) { log.LogWarning(e, "OIDC: Logout-Token an {App} nicht zugestellt", job.AppName); detail = "error"; }
        // Der Grund steht nur im Protokoll des Servers. Im Verlauf und in der Warteschlange steht nur "failed": der Status der Zieladresse waere sonst fuer den, der sie eintraegt, ein Auskunftsgeraet.
        log.LogWarning("OIDC: Logout-Token an {App} nicht zugestellt ({Detail}), Versuch {N}", job.AppName, detail, job.Attempts + 1);

        if (job.Attempts < Backoff.Length) await flow.RetryLogoutAsync(job.Id, now + Backoff[job.Attempts], "failed", ct);
        else
        {
            await flow.FinishLogoutAsync(job.Id, "failed", now, ct);
            await AuditAsync(job, "oidc.logout.failed", $"{job.AppName}: {job.UserName}");
        }
        return false;
    }

    private async Task AuditAsync(OidcLogoutJob job, string evt, string detail)
    {
        try { await audit.AddAsync("oidc", evt, detail, CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Verlauf der Sicherheit konnte nicht geschrieben werden"); }
    }
}
