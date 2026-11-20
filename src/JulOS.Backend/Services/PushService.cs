using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using Microsoft.Extensions.Logging;

namespace JulOS.Backend.Services;

public sealed record PushOutcome(int Sent, int Failed, int Removed, int Devices, bool Limited = false);

/// <summary>
/// Stellt eine Push-Nachricht an alle Geraete eines Benutzers zu (docs/PUSH.md): verschluesseln (RFC 8291), mit der VAPID-Kennung des Servers an den Endpunkt des Abos senden.
/// Je Versuch eine Frist von <see cref="Timeout"/>, bei Fehlern des Push-Dienstes (5xx, 429, keine Antwort) hoechstens zwei weitere Versuche. 404 und 410 heisst: das Abo ist tot, es wird entfernt.
/// Der Server ruft nur Endpunkte auf, die <see cref="PushEndpoint"/> annimmt, und verbindet nur zu oeffentlichen Adressen.
/// </summary>
public sealed class PushService(PushStore store, HttpClient http, HitCounter hits, TimeProvider time, ILogger<PushService> log)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const int MaxPerHour = 30;
    public const int Attempts = 3;      // der erste und hoechstens zwei Wiederholungen

    /// <summary>Nur fuer Tests: die Wartezeit vor einer Wiederholung (sonst 1 und 3 Sekunden).</summary>
    internal Func<int, CancellationToken, Task> Wait { get; init; } = (attempt, ct) => Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 1 : 3), ct);

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (ctx, ct) =>
        {
            // Die Adresse wird hier aufgeloest und geprueft, nicht vorher: so kann ein Name nicht zwischen Pruefung und Verbindung auf eine private Adresse zeigen
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var ok = addresses.Where(PushEndpoint.IsPublic).ToArray();
            if (ok.Length == 0) throw new HttpRequestException("Push-Endpunkt fuehrt nicht ins Internet.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(ok, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    }, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("JulOS", null) } } };

    /// <summary>Sendet <paramref name="payload"/> an alle Abos des Benutzers. Hoechstens <see cref="MaxPerHour"/> Sendungen je Benutzer und Stunde (Limited: nichts gesendet).</summary>
    public async Task<PushOutcome> SendAsync(Guid userId, byte[] payload, CancellationToken ct)
    {
        var subs = await store.SubscriptionsAsync(userId, ct);
        if (subs.Count == 0 || await store.VapidAsync(ct) is not { } vapid) return new(0, 0, 0, subs.Count);
        if (!hits.Allow("push:" + userId, MaxPerHour, TimeSpan.FromHours(1))) return new(0, 0, 0, subs.Count, true);
        int sent = 0, failed = 0, removed = 0;
        foreach (var sub in subs)
        {
            switch (await SendOneAsync(sub, payload, vapid, ct))
            {
                case Result.Ok: sent++; await store.MarkOkAsync(sub.Id, ct); break;
                case Result.Gone: removed++; await store.RemoveDeadAsync(sub.Id, ct); break;
                default: failed++; break;
            }
        }
        return new(sent, failed, removed, subs.Count);
    }

    private enum Result { Ok, Gone, Failed }

    private async Task<Result> SendOneAsync(PushSubscription sub, byte[] payload, (byte[] Private, byte[] Public) vapid, CancellationToken ct)
    {
        var endpoint = PushEndpoint.Parse(sub.Endpoint);
        if (endpoint is null) { log.LogWarning("Push: Endpunkt eines Abos nicht zulaessig, entfernt"); return Result.Gone; }
        byte[] body;
        try { body = WebPush.Encrypt(payload, sub.P256dh, sub.Auth); }
        catch (ArgumentException) { log.LogWarning("Push: Schluessel eines Abos ungueltig, entfernt"); return Result.Gone; }

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content.Headers.ContentEncoding.Add("aes128gcm");
            req.Headers.TryAddWithoutValidation("Authorization", WebPush.VapidHeader(vapid.Private, vapid.Public, endpoint, Subject(sub.Origin), time.GetUtcNow()));
            req.Headers.TryAddWithoutValidation("TTL", "43200");
            req.Headers.TryAddWithoutValidation("Urgency", "normal");
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(Timeout);
                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                var code = (int)res.StatusCode;
                if (code is >= 200 and < 300) return Result.Ok;
                if (code is 404 or 410) return Result.Gone;
                log.LogInformation("Push: Dienst antwortet mit {Status} (Versuch {Attempt})", code, attempt);
                if (code != 429 && code < 500) return Result.Failed;              // ein Fehler der Anfrage selbst (400, 401, 403, 413): nochmal hilft nicht
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                log.LogInformation("Push: keine Antwort des Dienstes (Versuch {Attempt}, {Type})", attempt, e.GetType().Name);
            }
            if (attempt < Attempts) await Wait(attempt, ct).ConfigureAwait(false);
        }
        return Result.Failed;
    }

    /// <summary>Absender der VAPID-Kennung: die Adresse der App (https) oder, ohne gueltige, ein neutraler Wert. Der Push-Dienst darf ihn zur Kontaktaufnahme nutzen (RFC 8292).</summary>
    private static string Subject(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.GetLeftPart(UriPartial.Authority) : "mailto:admin@example.invalid";
}
