using System.Net;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Backend.Modules;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Services;

/// <summary>Wie JulOS an die Adresse einer App schickt. Ein eigener Client: keine Weiterleitungen, kein Proxy, kurze Fristen (OutboundHttp). Tests setzen <see cref="Create"/>.</summary>
public sealed class OidcHttp
{
    public Func<HttpClient> Create { get; set; } = () => OutboundHttp.Create();
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

    /// <summary>Ein Durchgang: beendete Sitzungen einreihen, Faelliges zustellen, ab und zu aufraeumen. Gibt die Zahl der versuchten Zustellungen.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        await flow.EnqueueForEndedSessionsAsync(now, ct);
        var due = await flow.DueLogoutsAsync(now, 50, ct);
        foreach (var job in due) await DeliverAsync(job, ct);
        if (now - _lastPurge > TimeSpan.FromHours(1)) { _lastPurge = now; await flow.PurgeAsync(now, ct); await keys.PurgeAsync(ct); }
        return due.Count;
    }

    private async Task DeliverAsync(OidcLogoutJob job, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (job.Uri is null || issuer.Value is null || !keys.Available) { await flow.FinishLogoutAsync(job.Id, "no_uri", now, ct); return; }    // die App hat keine Adresse: es gibt nichts zuzustellen
        string result;
        try
        {
            var token = OidcTokens.CreateLogoutToken(new LogoutTokenClaims(issuer.Value, job.ClientId, job.Sub.ToString(), job.Sid, now, OidcRules.NewJti()), await keys.ActiveAsync(ct));
            using var client = http.Create();
            using var body = new FormUrlEncodedContent([new("logout_token", token)]);
            using var response = await client.PostAsync(job.Uri, body, ct);
            // 200 und 204 sind Erfolg; auch 3xx ist keiner (es wird nichts weitergeleitet)
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            {
                await flow.FinishLogoutAsync(job.Id, "ok", now, ct);
                await AuditAsync(job, "oidc.logout.ok", $"{job.AppName}: {job.UserName} ({job.Reason})");
                return;
            }
            result = "http " + (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { result = "timeout"; }
        catch (HttpRequestException) { result = "unreachable"; }
        catch (Exception e) { log.LogWarning(e, "OIDC: Logout-Token an {App} nicht zugestellt", job.AppName); result = "error"; }

        if (job.Attempts < Backoff.Length) await flow.RetryLogoutAsync(job.Id, now + Backoff[job.Attempts], result, ct);
        else
        {
            await flow.FinishLogoutAsync(job.Id, "failed: " + result, now, ct);
            await AuditAsync(job, "oidc.logout.failed", $"{job.AppName}: {job.UserName} ({result})");
        }
    }

    private async Task AuditAsync(OidcLogoutJob job, string evt, string detail)
    {
        try { await audit.AddAsync("oidc", evt, detail, CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Verlauf der Sicherheit konnte nicht geschrieben werden"); }
    }
}
