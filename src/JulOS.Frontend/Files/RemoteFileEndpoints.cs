using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using JulOS.Backend.Data;
using JulOS.Backend.Logic;
using JulOS.Frontend.Auth;
using JulOS.Frontend.Remote;
using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;

namespace JulOS.Frontend.Files;

/// <summary>Erzeugt eine SFTP-Sitzung (im Betrieb SSH.NET, in Tests ein Ersatz im Speicher).</summary>
internal sealed class FileSessionFactory(Func<ISftpFileSession> create)
{
    public ISftpFileSession Create() => create();
    public static FileSessionFactory Default { get; } = new(() => new SshNetFileSession());
}

/// <summary>
/// Dateien auf Hosts per SFTP (docs/FILES.md): die gespeicherten SSH-Zugangsdaten einer Verbindung, der Host-Schluessel wie beim Terminal (aber auch der erste muss bestaetigt werden),
/// alles hinter der Sitzung. Lesen immer, Schreiben nur, wenn der Schalter "Dateien schreiben" an ist (App, sonst JULOS_FILES_WRITE=on), und nur mit "confirm" in der Anfrage (die Seite fragt vorher). Jede schreibende Aktion und jeder Fehler
/// kommt in den Verlauf (files_audit), nie ein Inhalt oder Passwort. Alle Pfade sind absolut und werden hier normalisiert; Wirkung hat nur, was der SSH-Benutzer darf.
/// </summary>
internal static class RemoteFileEndpoints
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<Guid, int> Streams = new();       // laufende Down- und Uploads je Benutzer
    private const int MaxStreams = 3;
    /// <summary>So viele Eintraege zeigt eine Liste hoechstens. Der Host liefert sie in seiner Reihenfolge, ein Rest fehlt also zufaellig: darum grosszuegig (so viel wie ein Auftrag je Operation, FileLimits.MaxItems) und mit Hinweis auf der Seite.</summary>
    private const int ListLimit = 10_000;

    private static IResult Fail(string code)
    {
        var (status, text) = FileErrors.Describe(code);
        return Results.Text(JsonSerializer.Serialize(new FileErrorBody(text, code), AppJson.Default.FileErrorBody), "application/json", Encoding.UTF8, status);
    }

    private static IResult Fail(Exception e)
    {
        var code = FileErrors.Code(e);
        var (status, text) = FileErrors.Describe(code);
        var body = e switch
        {
            HostKeyProblem h => new FileErrorBody(text, code, h.Presented.Fingerprint, h.Presented.Algorithm, h.Known),
            PasswordRequiredException p => new FileErrorBody(text, code, Connection: p.Connection, User: p.HostLoginUser, CanSave: p.HostLoginUser is null ? null : true),
            _ => new FileErrorBody(text, code),
        };
        return Results.Text(JsonSerializer.Serialize(body, AppJson.Default.FileErrorBody), "application/json", Encoding.UTF8, status);
    }

    private static string Kind(RemoteKind k) => k switch { RemoteKind.File => "file", RemoteKind.Directory => "directory", RemoteKind.Link => "link", _ => "other" };

    private static RemoteEntryDto ToDto(RemoteEntry e) => new(e.Name, e.Path, Kind(e.Kind), e.Size, e.Modified, e.Mode, e.Kind == RemoteKind.Link && e.Resolved is { } r ? Kind(r) : null, e.IsOpenable);

    /// <summary>Alles, was ein Endpunkt zum Verbinden braucht.</summary>
    private sealed record Ctx(SessionIdentity Id, ConnectionRecord Connection, RootFiles? Root = null);

    private static bool RateLimited(HttpContext ctx, HitCounter hits, string what, int max) =>
        !hits.Allow($"files-{what}:" + ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId, max, Minute);

    private static async Task<(Ctx? Ctx, IResult? Fail)> ResolveAsync(HttpContext ctx, Guid? connection, ConnectionStore connections, HostPolicy policy)
    {
        var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
        if (connection is not { } cid) return (null, Fail("invalid_request"));
        var c = await connections.GetAsync(id.UserId, cid, ctx.RequestAborted);
        if (c is null)
        {
            // Kein SSH-Zugang mit dieser Kennung: vielleicht der Ort "Root" eines Hosts (die Kennung ist die des Hosts; der Agent des Hosts macht es, docs/FILES.md)
            var rf = ctx.RequestServices.GetRequiredService<RootFiles>();
            return await rf.PlaceAsync(id.UserId, cid, ctx.RequestAborted) is { } place ? (new Ctx(id, place, rf), null) : (null, Fail("connection_not_found"));
        }
        if (c.Kind != "terminal") return (null, Fail("not_ssh"));
        if (!policy.Allows(c.Host)) return (null, Fail("target_not_allowed"));
        return (new Ctx(id, c), null);
    }

    /// <summary>Leiht die Sitzung der Verbindung; gibt es keine, verbindet sich der Pool mit dem gespeicherten (oder dem getippten) Passwort.</summary>
    private static async Task<SftpPool.Lease> LeaseAsync(Ctx x, string? typedPassword, string? acceptHostKey, SftpPool pool, ConnectionStore connections, SecretBox box, FileSessionFactory factory, ILogger log, CancellationToken ct)
    {
        if (x.Root is { } rootFiles)
        {
            // Der Ort Root: keine Anmeldung, aber alle Schluessel werden hier und bei jeder Anfrage der Sitzung geprueft
            if (await rootFiles.GateAsync(x.Id.UserId, x.Connection.Id, ct) is { } closed) throw new RootGateException(closed);
            return pool.TryAcquire(x.Id.UserId, x.Connection.Id) ?? await pool.AcquireAsync(x.Id.UserId, x.Connection.Id, _ => Task.FromResult<ISftpFileSession>(rootFiles.NewSession(x.Id.UserId, x.Connection.Id)), ct);
        }
        if (pool.TryAcquire(x.Id.UserId, x.Connection.Id) is { } open) return open;
        var c = x.Connection;
        var password = typedPassword;
        // Die Zugangsdaten dieser Verbindung: ihr eigener Login, sonst der Login ihres Hosts (dann kommt keine Frage mehr), sonst wie bisher (docs/HOSTS.md)
        var login = await connections.ResolveLoginAsync(x.Id.UserId, c, box, ct);
        if (string.IsNullOrEmpty(password))
        {
            if (login.Unreadable) throw new PasswordRequiredException(c.Id);          // das gespeicherte Passwort ist nicht lesbar: neu eingeben
            password = login.Password;
        }
        if (string.IsNullOrEmpty(password)) throw new PasswordRequiredException(c.Id, login.HostLoginWithoutPassword ? login.Username : null);
        return await pool.AcquireAsync(x.Id.UserId, c.Id, async _ =>
        {
            var session = factory.Create();
            SshHostKey? presented = null;
            HostKeyDecision decision = HostKeyDecision.Known;
            try
            {
                // Strenger als beim Terminal: auch der ERSTE Schluessel gilt erst nach ausdruecklicher Bestaetigung (acceptHostKey = genau dieser Schluessel)
                await session.ConnectAsync(new SftpConnectOptions(c.Host, c.Port, login.Username, password, null, null) { ConnectTimeout = TimeSpan.FromSeconds(15) }, key =>
                {
                    presented = key;
                    decision = HostKeyPolicy.Decide(c.HostKey, acceptHostKey, key);
                    return decision != HostKeyDecision.Changed && (decision != HostKeyDecision.FirstUse || string.Equals(acceptHostKey, key.Id, StringComparison.Ordinal));
                }, ct);
            }
            catch (SshException e) when (e.Failure == SshFailure.HostKeyRejected && (presented ?? session.PresentedHostKey) is { } shown)
            {
                await session.DisposeAsync();
                throw new HostKeyProblem(decision == HostKeyDecision.Changed, shown, c.HostKey);
            }
            catch { await session.DisposeAsync(); throw; }
            if (decision is HostKeyDecision.FirstUse or HostKeyDecision.AcceptedChange && session.PresentedHostKey is { } key)
            {
                try { await connections.SetHostKeyAsync(x.Id.UserId, c.Id, key.Id, CancellationToken.None); }
                catch (Exception e) { log.LogWarning(e, "SFTP-Host-Schlüssel konnte nicht gemerkt werden"); }
                log.LogInformation("SFTP-Host-Schlüssel {Decision} ({Algorithm})", decision == HostKeyDecision.FirstUse ? "gemerkt" : "ersetzt (vom Benutzer bestätigt)", key.Algorithm);
            }
            return session;
        }, ct);
    }

    /// <summary>Legt das eben erfolgreich benutzte Passwort als Passwort des Host-Logins ab (verschluesselt, gebunden an die Adresse dieser Verbindung). Der Eintrag im Verlauf steht davor; ohne ihn wird nichts gespeichert.</summary>
    private static async Task<bool> SaveHostPasswordAsync(Ctx x, string password, ConnectionStore connections, HostStore hosts, HostLoginStore hostLogins, HostAuditStore hostAudit, SecretBox box, ILogger log, CancellationToken ct)
    {
        var c = x.Connection;
        var login = await connections.ResolveLoginAsync(x.Id.UserId, c, box, ct);
        if (!login.HostLoginWithoutPassword || await hosts.GetAsync(x.Id.UserId, c.HostId, ct) is not { } host) return false;
        try { await hostAudit.AddAsync(x.Id.UserId, host.Id, host.Name, "login.set", null, "ok", "password (files)", ct: CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Verlauf des Host-Logins konnte nicht geschrieben werden"); return false; }
        return await hostLogins.SetPasswordAsync(x.Id.UserId, host.Id, login.Username, box.Encrypt(password, host.Id), [LoginResolver.Address(c.Host)], ct);
    }

    private static string HostName(ConnectionRecord c) => c.Name;

    private static async Task AuditAsync(FileAuditStore audit, ILogger log, Guid user, ConnectionRecord c, string action, string? path, string result, string? detail = null, int? items = null, string? host = null)
    {
        try { await audit.AddAsync(user, c.Id, host ?? HostName(c), action, path, result, detail, items, CancellationToken.None); }
        catch (Exception e) { log.LogError(e, "Verlauf der Dateien konnte nicht geschrieben werden"); }
    }

    private static string Describe(string[]? paths) => paths is { Length: > 0 } ? (paths.Length == 1 ? paths[0] : $"{paths[0]} (+{paths.Length - 1})") : "";

    public static void MapRemoteFiles(this IEndpointRouteBuilder app)
    {
        // Was die Seite wissen muss: gibt es den lokalen Ort, darf geschrieben werden, wie gross darf etwas sein
        app.MapGet("/api/files/info", (FileBrowser local, FilesOptions options, SecurityConfig security) =>
            Results.Json(new FilesInfoDto(local.Enabled, security.FilesWrite.Enabled, options.MaxBytes), AppJson.Default.FilesInfoDto));

        // Oeffnet (oder haelt offen) die SFTP-Sitzung einer Verbindung. Mit "password" wird getippt (nie gespeichert, nie geloggt), mit "acceptHostKey" ein neuer oder geaenderter Schluessel bestaetigt.
        app.MapPost("/api/files/remote/open", async (OpenRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, ILogger<FileAuditStore> log,
            HostStore hosts, HostLoginStore hostLogins, HostAuditStore hostAudit) =>
        {
            if (RateLimited(ctx, hits, "open", 20)) return Fail("rate_limited");
            if (body.Password is { Length: > 256 }) return Fail("invalid_request");
            var (x, fail) = await ResolveAsync(ctx, body.Connection, connections, policy);
            if (x is null) return fail!;
            try
            {
                var known = x.Connection.HostKey;
                var wasOpen = pool.IsOpen(x.Id.UserId, x.Connection.Id);
                await using var lease = await LeaseAsync(x, body.Password, body.AcceptHostKey, pool, connections, box, factory, log, ctx.RequestAborted);
                if (x.Root is not null) return Results.Json(new OpenResponse(lease.Session.HomeDirectory, "root", null, null), AppJson.Default.OpenResponse);
                var key = lease.Session.PresentedHostKey;
                var state = wasOpen || key is null ? "known" : known is null ? "remembered" : key.Id != known ? "replaced" : "known";
                // "Fuer diesen Host speichern": erst nach gelungener Anmeldung mit genau diesem Passwort, und nur wenn der Host schon einen Login mit Benutzer, aber ohne Passwort hat (nie ein neuer Benutzer von hier)
                var saved = !wasOpen && body.SaveForHost == true && !string.IsNullOrEmpty(body.Password) && box.Enabled
                    && await SaveHostPasswordAsync(x, body.Password, connections, hosts, hostLogins, hostAudit, box, log, ctx.RequestAborted);
                return Results.Json(new OpenResponse(lease.Session.HomeDirectory, state, key?.Fingerprint, key?.Algorithm, saved ? true : null), AppJson.Default.OpenResponse);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "open", null, "failed", FileErrors.Code(e));
                return Fail(e);
            }
        });

        app.MapPost("/api/files/remote/close", async (CloseRequest body, HttpContext ctx, SftpPool pool) =>
        {
            var id = (SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!;
            if (body.Connection is { } c) await pool.CloseAsync(id.UserId, c);
            return Results.NoContent();
        });

        app.MapGet("/api/files/remote/list", async (Guid? c, string? path, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FilesOptions options, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "list", 240)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, c, connections, policy);
            if (x is null) return fail!;
            try
            {
                await using var lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                var dir = RemotePath.Normalize(path, lease.Session.HomeDirectory);
                if (dir is null) return Fail("invalid_path");
                RemoteListing listing;
                try { listing = await lease.Session.ListAsync(dir, ListLimit, ctx.RequestAborted); }
                catch (RemoteFileException ex) when (ex.Error == RemoteFileError.NotFound)
                {
                    // SFTP meldet bei einer Datei "nicht gefunden"; fuer die Seite ist es ein Pfad, der kein Ordner ist
                    if (await lease.Session.StatAsync(dir, follow: true, ctx.RequestAborted) is { Kind: not RemoteKind.Directory }) throw new RemoteFileException(RemoteFileError.NotADirectory, "Das ist kein Ordner.");
                    throw;
                }
                var sorted = listing.Entries.OrderBy(e => e.IsOpenable ? 0 : 1).ThenBy(e => e.Name, Comparer<string>.Create(RemotePath.NaturalCompare)).Select(ToDto).ToArray();
                return Results.Json(new RemoteListingDto(dir, sorted, listing.Truncated, listing.Hidden), AppJson.Default.RemoteListingDto);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "list", path, "failed", FileErrors.Code(e));
                return Fail(e);
            }
        });

        // Textansicht: hoechstens 1 MiB, nur Text (UTF-8, kein NUL); die Antwort ist JSON mit dem Text als Wert, die Seite setzt ihn als Text ein, nie als HTML
        app.MapGet("/api/files/remote/text", async (Guid? c, string? path, string? token, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "text", 120)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, c, connections, policy);
            if (x is null) return fail!;
            try
            {
                await using var lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                var file = RemotePath.Normalize(path, null);
                if (file is null || file == "/") return Fail("invalid_path");
                // Root liest alles: auch Ansehen braucht die Bestaetigung vom Server und steht im Verlauf (started, dann das Ergebnis)
                if (x.Root is not null)
                {
                    var (refused, level) = await RedeemAsync(conf, audit, log, x, token, FileConfirmations.Canon("text", x.Connection.Id, null, [file], null, null), "text", null, false, false, file);
                    if (refused is not null) return refused;
                    if (await StartedAsync(audit, log, x, "text", file, 1, null, level) is { } stopped) return stopped;
                }
                var preview = await FileTransfer.ReadTextAsync(lease.Session, file, FileLimits.Default, ctx.RequestAborted);
                if (x.Root is not null) await AuditAsync(audit, log, x.Id.UserId, x.Connection, "text", file, "ok", null, 1);
                return Results.Json(new TextDto(file, preview.Text, preview.Truncated, preview.Size), AppJson.Default.TextDto);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "text", path, "failed", FileErrors.Code(e));
                return Fail(e);
            }
        });

        // Download im Strom: nie die ganze Datei im Speicher; immer als Anhang und als unbekannter Typ (nie als Seite dieser Adresse)
        app.MapGet("/api/files/remote/download", async (Guid? c, string? path, string? token, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FilesOptions options, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "transfer", 60)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, c, connections, policy);
            if (x is null) return fail!;
            if (!TryEnterStream(x.Id.UserId)) return Fail("busy");
            try
            {
                await using var lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                var file = RemotePath.Normalize(path, null);
                if (file is null || file == "/") return Fail("invalid_path");
                var entry = await lease.Session.StatAsync(file, follow: true, ctx.RequestAborted);
                if (entry is null) return Fail("not_found");
                if (entry.Kind != RemoteKind.File) return Fail("not_a_file");
                if (entry.Size > options.MaxBytes) return Fail("too_large");
                if (x.Root is not null)
                {
                    var (refused, level) = await RedeemAsync(conf, audit, log, x, token, FileConfirmations.Canon("download", x.Connection.Id, null, [file], null, null), "download", null, false, false, file);
                    if (refused is not null) return refused;
                    if (await StartedAsync(audit, log, x, "download", file, 1, null, level) is { } stopped) return stopped;
                }
                await using var stream = await lease.Session.OpenReadAsync(file, ctx.RequestAborted);
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Headers.ContentDisposition = RemoteEndpoints.ContentDisposition(RemotePath.NameOf(file));
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.ContentLength = entry.Size;
                var buffer = new byte[FileLimits.Default.BufferSize];
                long sent = 0;
                try
                {
                    while (sent < entry.Size)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, entry.Size - sent)), ctx.RequestAborted);
                        if (n == 0) break;
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                        sent += n;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException && ctx.Response.HasStarted)
                {
                    await AuditAsync(audit, log, x.Id.UserId, x.Connection, "download", file, "failed", FileErrors.Code(e));
                    ctx.Abort();                              // der Browser soll einen Fehler sehen, nicht eine unvollstaendige Datei fuer vollstaendig halten
                    return Results.Empty;
                }
                if (sent < entry.Size) { await AuditAsync(audit, log, x.Id.UserId, x.Connection, "download", file, "failed", "incomplete"); ctx.Abort(); }
                else if (x.Root is not null) await AuditAsync(audit, log, x.Id.UserId, x.Connection, "download", file, "ok", null, 1);
                return Results.Empty;
            }
            catch (Exception e) when (e is not OperationCanceledException && !ctx.Response.HasStarted)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "download", path, "failed", FileErrors.Code(e));
                return Fail(e);
            }
            finally { LeaveStream(x.Id.UserId); }
        });

        // Upload im Strom (roher Koerper): erst unter einem temporaeren Namen, dann umbenennen. Ein vorhandenes Ziel nur mit overwrite=1; confirm=1 sagt, dass die Seite gefragt hat.
        app.MapPost("/api/files/remote/upload", async (Guid? c, string? dir, string? name, string? overwrite, string? token, string? acknowledged, string? typed, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FilesOptions options, SecurityConfig security, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "transfer", 60)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, c, connections, policy);
            if (x is null) return fail!;
            var denied = await WriteOnAsync(security, audit, log, x, "upload", dir);
            if (denied is not null) return denied;
            if (!RemotePath.ValidName(name)) return Fail("invalid_name");
            if (ctx.Request.ContentLength is > 0 and var len && len > options.MaxBytes) return Fail("too_large");
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = options.MaxBytes;
            if (!TryEnterStream(x.Id.UserId)) return Fail("busy");
            string? target = null;
            try
            {
                await using var lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                var folder = RemotePath.Normalize(dir, lease.Session.HomeDirectory);
                if (folder is null) return Fail("invalid_path");
                target = RemotePath.Join(folder, name!);
                var there = await lease.Session.StatAsync(folder, follow: true, ctx.RequestAborted);
                if (there is null) return Fail("not_found");
                if (there.Kind != RemoteKind.Directory) return Fail("not_a_directory");
                var (refused, level) = await RedeemAsync(conf, audit, log, x, token, FileConfirmations.Canon("upload", x.Connection.Id, null, [], folder, null), "upload", name, false, overwrite == "1", target, new ConfirmAnswer(acknowledged == "1", typed));
                if (refused is not null) return refused;
                var existed = await lease.Session.StatAsync(target, follow: false, ctx.RequestAborted) is not null;
                if (await StartedAsync(audit, log, x, "upload", target, 1, null, level) is { } stopped) return stopped;
                var bytes = await FileTransfer.WriteAtomicAsync(lease.Session, target, ctx.Request.Body, overwrite == "1", options.MaxBytes, FileLimits.Default.BufferSize, null, ctx.RequestAborted);
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "upload", target, "ok", existed ? "overwrite" : null, 1);
                return Results.Json(new UploadedDto(target, bytes, existed), AppJson.Default.UploadedDto);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException) { await AuditAsync(audit, log, x.Id.UserId, x.Connection, "upload", target ?? dir, "failed", "too_large"); return Fail("too_large"); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "upload", target ?? dir, "failed", FileErrors.Code(e));
                return Fail(e);
            }
            finally { LeaveStream(x.Id.UserId); }
        });

        app.MapPost("/api/files/remote/mkdir", async (MkdirRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FilesOptions options, SecurityConfig security, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "write", 120)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, body.Connection, connections, policy);
            if (x is null) return fail!;
            var denied = await WriteOnAsync(security, audit, log, x, "mkdir", body.Dir);
            if (denied is not null) return denied;
            if (!RemotePath.ValidName(body.Name)) return Fail("invalid_name");
            string? target = null;
            try
            {
                await using var lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                var folder = RemotePath.Normalize(body.Dir, lease.Session.HomeDirectory);
                if (folder is null) return Fail("invalid_path");
                target = RemotePath.Join(folder, body.Name!);
                var (refused, level) = await RedeemAsync(conf, audit, log, x, body.Token, FileConfirmations.Canon("mkdir", x.Connection.Id, null, [], folder, body.Name), "mkdir", null, false, false, target, new ConfirmAnswer(body.Acknowledged == true, body.Typed));
                if (refused is not null) return refused;
                if (await StartedAsync(audit, log, x, "mkdir", target, 1, null, level) is { } stopped) return stopped;
                await lease.Session.CreateDirectoryAsync(target, ctx.RequestAborted);
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "mkdir", target, "ok", null, 1);
                return Results.Json(new PathDto(target), AppJson.Default.PathDto);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "mkdir", target ?? body.Dir, "failed", FileErrors.Code(e));
                return Fail(e);
            }
        });

        // Umbenennen im selben Ordner (Verschieben in einen anderen Ordner ist "copy" mit move=true)
        app.MapPost("/api/files/remote/rename", async (RenameRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FilesOptions options, SecurityConfig security, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "write", 120)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, body.Connection, connections, policy);
            if (x is null) return fail!;
            var denied = await WriteOnAsync(security, audit, log, x, "rename", body.Path);
            if (denied is not null) return denied;
            if (!RemotePath.ValidName(body.Name)) return Fail("invalid_name");
            try
            {
                await using var lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                var from = RemotePath.Normalize(body.Path, null);
                if (from is null || from == "/") return Fail("invalid_path");
                var to = RemotePath.Join(RemotePath.Parent(from), body.Name!);
                if (to == from) return Fail("same_place");
                var (refused, level) = await RedeemAsync(conf, audit, log, x, body.Token, FileConfirmations.Canon("rename", x.Connection.Id, null, [from], null, body.Name), "rename", null, false, body.Overwrite == true, from, new ConfirmAnswer(body.Acknowledged == true, body.Typed));
                if (refused is not null) return refused;
                var source = await lease.Session.StatAsync(from, follow: false, ctx.RequestAborted);
                if (source is null) return Fail("not_found");
                var there = await lease.Session.StatAsync(to, follow: false, ctx.RequestAborted);
                // Ersetzen nur eine Datei durch eine Datei und nur mit overwrite; ein Ordner wird nie ersetzt
                if (there is not null && (there.Kind == RemoteKind.Link || source.Kind == RemoteKind.Link)) return Fail("target_is_link");     // ein Link wird nie ersetzt und ersetzt nie etwas
                if (there is not null && (body.Overwrite != true)) return Fail("exists");
                if (there is not null && (there.Kind == RemoteKind.Directory || source.Kind == RemoteKind.Directory)) return Fail("type_mismatch");
                if (await StartedAsync(audit, log, x, "rename", $"{from} → {body.Name}", 1, null, level) is { } stopped) return stopped;
                await lease.Session.RenameAsync(from, to, body.Overwrite == true, source.Kind == RemoteKind.Link, ctx.RequestAborted);
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "rename", $"{from} → {body.Name}", "ok", there is not null ? "overwrite" : null, 1);
                return Results.Json(new PathDto(to), AppJson.Default.PathDto);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, "rename", body.Path, "failed", FileErrors.Code(e));
                return Fail(e);
            }
        });

        // Schritt 1 jeder schreibenden Aktion: der Server prueft alles (Schalter, Verbindung, Pfade, Zahlen, Namen, die ersetzt wuerden, ein Ordner mit Inhalt) und stellt eine kurzlebige, einmalige Bestaetigung aus,
        // gebunden an Sitzung, Aktion und diese Anfrage; die Seite zeigt genau diese Angaben. Erst der zweite Aufruf (upload, mkdir, rename, delete, copy) fuehrt mit dem Token aus.
        app.MapPost("/api/files/remote/prepare", async (PrepareRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, HitCounter hits, FilesOptions options, FileConfirmations conf, SecurityConfig security, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "prepare", 120)) return Fail("rate_limited");
            var action = body.Action;
            if (action is not ("upload" or "mkdir" or "rename" or "delete" or "copy" or "move" or "download" or "text")) return Fail("invalid_request");
            var (x, fail) = await ResolveAsync(ctx, body.Connection, connections, policy);
            if (x is null) return fail!;
            var reads = action is "download" or "text";
            if (reads && x.Root is null) return Fail("invalid_request");                      // nur der Ort Root verlangt eine Bestaetigung zum Lesen
            var denied = reads ? null : await WriteOnAsync(security, audit, log, x, action, body.Paths is { Length: > 0 } ps ? Describe(ps) : body.Dir);
            if (denied is not null) return denied;
            await using var leases = new Leases();
            try
            {
                var src = await leases.AddAsync(LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted));
                var home = src.Session.HomeDirectory;
                string[] paths = [];
                string? dir = null, name = null, targetHost = null;
                string[]? names = null;
                Guid? targetConnection = null;
                var counts = new TreeCount(0, 0, 0, 0);
                var conflicts = new List<string>();
                var nonEmpty = false;
                var targetRoot = false;
                switch (action)
                {
                    case "upload":
                    {
                        names = body.Names is { Length: > 0 and <= 100 } n && n.All(RemotePath.ValidName) ? [.. n.Distinct()] : null;
                        if (names is null) return Fail("invalid_name");
                        dir = RemotePath.Normalize(body.Dir, home);
                        if (dir is null) return Fail("invalid_path");
                        var there = await src.Session.StatAsync(dir, follow: true, ctx.RequestAborted);
                        if (there is null) return Fail("not_found");
                        if (there.Kind != RemoteKind.Directory) return Fail("not_a_directory");
                        foreach (var f in names) if (await src.Session.StatAsync(RemotePath.Join(dir, f), follow: false, ctx.RequestAborted) is not null) conflicts.Add(f);
                        counts = new TreeCount(names.Length, 0, 0, 0);
                        break;
                    }
                    case "mkdir":
                    {
                        if (!RemotePath.ValidName(body.Name)) return Fail("invalid_name");
                        dir = RemotePath.Normalize(body.Dir, home);
                        if (dir is null) return Fail("invalid_path");
                        name = body.Name;
                        counts = new TreeCount(0, 1, 0, 0);
                        break;
                    }
                    case "rename":
                    {
                        if (!RemotePath.ValidName(body.Name) || body.Paths is not { Length: 1 }) return Fail(body.Paths is { Length: 1 } ? "invalid_name" : "invalid_request");
                        var from = RemotePath.Normalize(body.Paths[0], null);
                        if (from is null || from == "/") return Fail("invalid_path");
                        paths = [from]; name = body.Name;
                        var source = await src.Session.StatAsync(from, follow: false, ctx.RequestAborted);
                        if (source is null) return Fail("not_found");
                        if (await src.Session.StatAsync(RemotePath.Join(RemotePath.Parent(from), name!), follow: false, ctx.RequestAborted) is not null) conflicts.Add(name!);
                        counts = new TreeCount(source.Kind == RemoteKind.File ? 1 : 0, source.Kind == RemoteKind.Directory ? 1 : 0, source.Kind == RemoteKind.Link ? 1 : 0, source.Size);
                        break;
                    }
                    case "download" or "text":
                    {
                        if (body.Paths is not { Length: 1 } || RemotePath.Normalize(body.Paths[0], null) is not { } file || file == "/") return Fail("invalid_path");
                        var entry = await src.Session.StatAsync(file, follow: true, ctx.RequestAborted);
                        if (entry is null) return Fail("not_found");
                        if (entry.Kind != RemoteKind.File) return Fail("not_a_file");
                        if (action == "download" && entry.Size > options.MaxBytes) return Fail("too_large");
                        paths = [file];
                        counts = new TreeCount(1, 0, 0, entry.Size);
                        break;
                    }
                    default:                                                   // delete, copy, move
                    {
                        if (body.Paths is not { Length: > 0 and <= 500 }) return Fail("invalid_request");
                        var normalized = NormalizeAll(body.Paths);
                        if (normalized is null) return Fail("invalid_path");
                        paths = normalized;
                        counts = await FileTransfer.CountAsync(src.Session, paths, Limits(options), ctx.RequestAborted);
                        if (action == "delete")
                        {
                            foreach (var p in paths)
                                if (await src.Session.StatAsync(p, follow: false, ctx.RequestAborted) is { Kind: RemoteKind.Directory } && (await src.Session.ListAsync(p, 1, ctx.RequestAborted)).Entries.Count > 0) nonEmpty = true;
                            break;
                        }
                        if (body.Target is not { } t) return Fail("invalid_request");
                        var same = t.Connection == x.Connection.Id;
                        var (tx, tfail) = same ? (x, null) : await ResolveAsync(ctx, t.Connection, connections, policy);
                        if (tx is null) return tfail!;
                        var dst = same ? src : await leases.AddAsync(LeaseAsync(tx, null, null, pool, connections, box, factory, log, ctx.RequestAborted));
                        dir = RemotePath.Normalize(t.Dir, dst.Session.HomeDirectory);
                        if (dir is null) return Fail("invalid_path");
                        targetConnection = t.Connection;
                        targetHost = same ? null : HostName(tx.Connection);
                        targetRoot = tx.Root is not null;
                        foreach (var p in paths)
                            if (await dst.Session.StatAsync(RemotePath.Join(dir, RemotePath.NameOf(p)), follow: false, ctx.RequestAborted) is not null) conflicts.Add(RemotePath.NameOf(p));
                        break;
                    }
                }
                // Die Stufe (docs/FILES.md): was der Server hier festgestellt hat bestimmt, was der zweite Schritt verlangt (nichts, das Haekchen oder den Namen); der Browser zeigt es nur
                var confirm = ConfirmPolicy.ForFiles(new FileFacts(action, x.Root is not null, targetRoot, paths, conflicts.Count > 0, nonEmpty), security.ConfirmMode);
                var expected = confirm.Demand == ConfirmDemand.Type ? HostName(x.Connection) : null;
                var token = conf.Issue(x.Id, FileConfirmations.Canon(action, x.Connection.Id, targetConnection, paths, dir, name), action, nonEmpty, conflicts.Count > 0, names, confirm, expected);
                if (token is null) return Fail("too_many_pending");
                return Results.Json(new PrepareResponse(token, (int)conf.Lifetime.TotalSeconds, action, HostName(x.Connection), targetHost, paths, dir, name, names, counts.Files, counts.Directories, counts.Links, counts.Bytes, [.. conflicts], nonEmpty, x.Root is not null || (targetConnection is { } tc2 && tc2 != x.Connection.Id && targetRoot),
                    confirm.LevelName, confirm.ModeName, confirm.Warn, confirm.Critical, expected), AppJson.Default.PrepareResponse);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await AuditAsync(audit, log, x.Id.UserId, x.Connection, action, body.Paths is { Length: > 0 } q ? Describe(q) : body.Dir, "failed", FileErrors.Code(e));
                return Fail(e);
            }
        });

        app.MapPost("/api/files/remote/delete", async (DeleteRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, FileJobs jobs, HitCounter hits, FilesOptions options, SecurityConfig security, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "write", 120)) return Fail("rate_limited");
            var (x, fail) = await ResolveAsync(ctx, body.Connection, connections, policy);
            if (x is null) return fail!;
            var denied = await WriteOnAsync(security, audit, log, x, "delete", Describe(body.Paths));
            if (denied is not null) return denied;
            if (body.Paths is not { Length: > 0 and <= 500 }) return Fail("invalid_request");
            var paths = NormalizeAll(body.Paths);
            if (paths is null) return Fail("invalid_path");
            SftpPool.Lease lease;
            try { lease = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted); }
            catch (Exception e) when (e is not OperationCanceledException) { await AuditAsync(audit, log, x.Id.UserId, x.Connection, "delete", Describe(paths), "failed", FileErrors.Code(e)); return Fail(e); }
            var recursive = body.Recursive == true;
            // Erst die Sitzung (ein Fehler dabei verbraucht die Bestaetigung nicht), dann einloesen, dann der Eintrag "started"
            var (refused, level) = await RedeemAsync(conf, audit, log, x, body.Token, FileConfirmations.Canon("delete", x.Connection.Id, null, paths, null, null), "delete", null, recursive, false, Describe(paths), new ConfirmAnswer(body.Acknowledged == true, body.Typed));
            if (refused is null) refused = await StartedAsync(audit, log, x, "delete", Describe(paths), null, null, level);
            if (refused is not null) { await lease.DisposeAsync(); return refused; }
            var job = jobs.Start(x.Id.UserId, "delete", HostName(x.Connection), async (j, ct) =>
            {
                await using (lease) j.Result = await FileTransfer.DeleteAsync(lease.Session, paths, recursive, Limits(options), j.Progress, ct);
            }, j => AuditJob(audit, log, j, x.Connection, "delete", Describe(paths), recursive ? "recursive" : null));
            if (job is null) { await lease.DisposeAsync(); return Fail("busy"); }
            return Results.Json(new JobStartedDto(job.Id), AppJson.Default.JobStartedDto, statusCode: 202);
        });

        // Kopieren und Verschieben, auch von Host zu Host: der Server liest per SFTP von der Quelle und schreibt per SFTP aufs Ziel (im Strom, erst unter temporaerem Namen)
        app.MapPost("/api/files/remote/copy", async (CopyRequest body, HttpContext ctx, ConnectionStore connections, HostPolicy policy, SftpPool pool, SecretBox box, FileSessionFactory factory, FileAuditStore audit, FileJobs jobs, HitCounter hits, FilesOptions options, SecurityConfig security, FileConfirmations conf, ILogger<FileAuditStore> log) =>
        {
            if (RateLimited(ctx, hits, "write", 120)) return Fail("rate_limited");
            var action = body.Move == true ? "move" : "copy";
            var (x, fail) = await ResolveAsync(ctx, body.Connection, connections, policy);
            if (x is null) return fail!;
            var denied = await WriteOnAsync(security, audit, log, x, action, Describe(body.Paths));
            if (denied is not null) return denied;
            if (body.Paths is not { Length: > 0 and <= 500 } || body.Target is not { } t) return Fail("invalid_request");
            var paths = NormalizeAll(body.Paths);
            if (paths is null) return Fail("invalid_path");
            var same = t.Connection == x.Connection.Id;
            var (tx, tfail) = same ? (x, null) : await ResolveAsync(ctx, t.Connection, connections, policy);
            if (tx is null) return tfail!;
            SftpPool.Lease src, dst;
            try
            {
                src = await LeaseAsync(x, null, null, pool, connections, box, factory, log, ctx.RequestAborted);
                try { dst = same ? src : await LeaseAsync(tx, null, null, pool, connections, box, factory, log, ctx.RequestAborted); }
                catch { await src.DisposeAsync(); throw; }
            }
            catch (Exception e) when (e is not OperationCanceledException) { await AuditAsync(audit, log, x.Id.UserId, x.Connection, action, Describe(paths), "failed", FileErrors.Code(e)); return Fail(e); }
            var dir = RemotePath.Normalize(t.Dir, dst.Session.HomeDirectory);
            if (dir is null) { await src.DisposeAsync(); if (!same) await dst.DisposeAsync(); return Fail("invalid_path"); }
            var move = body.Move == true;
            var overwrite = body.Overwrite == true;
            var host = same ? HostName(x.Connection) : $"{HostName(x.Connection)} → {HostName(tx.Connection)}";
            var (refused, level) = await RedeemAsync(conf, audit, log, x, body.Token, FileConfirmations.Canon(action, x.Connection.Id, t.Connection, paths, dir, null), action, null, false, overwrite, $"{Describe(paths)} → {dir}", new ConfirmAnswer(body.Acknowledged == true, body.Typed));
            if (refused is null) refused = await StartedAsync(audit, log, x, action, $"{Describe(paths)} → {dir}", null, host, level);
            if (refused is not null) { await src.DisposeAsync(); if (!same) await dst.DisposeAsync(); return refused; }
            var job = jobs.Start(x.Id.UserId, action, host, async (j, ct) =>
            {
                await using (src) await using (same ? null : dst)
                    j.Result = move ? await FileTransfer.MoveAsync(src.Session, dst.Session, paths, dir, same, overwrite, Limits(options), j.Progress, ct)
                                    : await FileTransfer.CopyAsync(src.Session, dst.Session, paths, dir, same, overwrite, Limits(options), j.Progress, ct);
            }, j => AuditJob(audit, log, j, x.Connection, action, $"{Describe(paths)} → {dir}", overwrite ? "overwrite" : null, host));
            if (job is null) { await src.DisposeAsync(); if (!same) await dst.DisposeAsync(); return Fail("busy"); }
            return Results.Json(new JobStartedDto(job.Id), AppJson.Default.JobStartedDto, statusCode: 202);
        });

        app.MapGet("/api/files/remote/jobs/{id:guid}", (Guid id, HttpContext ctx, FileJobs jobs) =>
        {
            var user = ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId;
            return jobs.Get(user, id) is { } j ? Results.Json(ToDto(j), AppJson.Default.JobDto) : Fail("job_not_found");
        });

        app.MapPost("/api/files/remote/jobs/{id:guid}/cancel", (Guid id, HttpContext ctx, FileJobs jobs) =>
        {
            var user = ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId;
            return jobs.Cancel(user, id) ? Results.NoContent() : Fail("job_not_found");
        });

        app.MapGet("/api/files/audit", async (HttpContext ctx, FileAuditStore audit, HitCounter hits) =>
        {
            if (RateLimited(ctx, hits, "audit", 60)) return Fail("rate_limited");
            var user = ((SessionIdentity)ctx.Items[ApiGuard.IdentityKey]!).UserId;
            var limit = int.TryParse(ctx.Request.Query["limit"], out var n) ? n : 50;
            var list = await audit.ListAsync(user, limit, ctx.RequestAborted);
            return Results.Json(new FileAuditDto([.. list.Select(e => new FileAuditEntryDto(e.At, e.Host, e.Action, e.Path, e.Result, e.Detail, e.Items))]), AppJson.Default.FileAuditDto);
        });
    }

    private static FileLimits Limits(FilesOptions o) => FileLimits.Default with { MaxBytes = o.MaxBytes };

    private static string[]? NormalizeAll(string[] paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            var n = RemotePath.Normalize(p, null);
            if (n is null) return null;
            list.Add(n);
        }
        return [.. list.Distinct()];
    }

    private static JobDto ToDto(FileJob j) => new(j.Id, j.Kind, j.State, j.Error, j.Progress.ItemsDone, j.Progress.ItemsTotal, j.Progress.BytesDone, j.Progress.BytesTotal, j.Progress.Skipped, j.Progress.SkippedExisting, j.Progress.Current,
        j.Result is { } r ? new JobResultDto(r.Files, r.Directories) : null);

    private static Task AuditJob(FileAuditStore audit, ILogger log, FileJob j, ConnectionRecord c, string action, string path, string? detail, string? host = null)
    {
        var result = j.Outcome switch { "done" => "ok", "cancelled" => "cancelled", _ => "failed" };
        var items = j.Result is { } r ? r.Files + r.Directories : j.Progress.ItemsDone;
        var d = j.Outcome == "failed" ? j.Error : detail;
        return AuditAsync(audit, log, j.UserId, c, action, path, result, d, items, host);
    }

    /// <summary>Schreiben nur, wenn es in den Einstellungen (Sicherheit) oder mit JULOS_FILES_WRITE=on eingeschaltet ist. Eine Ablehnung wegen des Schalters kommt in den Verlauf.</summary>
    private static async Task<IResult?> WriteOnAsync(SecurityConfig security, FileAuditStore audit, ILogger log, Ctx x, string action, string? path)
    {
        if (security.FilesWrite.Enabled) return null;
        await AuditAsync(audit, log, x.Id.UserId, x.Connection, action, path, "denied", "write_off");
        return Fail("write_off");
    }

    /// <summary>
    /// Loest die Bestaetigung ein (Schritt 2). Unbekannt, benutzt, abgelaufen oder fuer etwas anderes ausgestellt (andere Sitzung, Aktion, Pfade, Ziel, Name): abgelehnt und im Verlauf. Eine Ausfuehrung darf
    /// recursive oder overwrite nur verlangen, wenn der Server beides beim Vorbereiten gezeigt hat; sonst gilt die Bestaetigung als fuer etwas anderes ausgestellt.
    /// </summary>
    private static async Task<(IResult? Refused, string? Level)> RedeemAsync(FileConfirmations conf, FileAuditStore audit, ILogger log, Ctx x, string? token, string canon, string action, string? uploadName, bool recursive, bool overwrite, string? path, ConfirmAnswer? answer = null)
    {
        var r = conf.Take(x.Id, token, canon, action, uploadName, out var c, answer);
        if (r == ConfirmResult.Ok && ((recursive && !c!.NeedsRecursive) || (overwrite && !c!.NeedsOverwrite))) r = ConfirmResult.Mismatch;
        if (r == ConfirmResult.Ok) return (null, c!.Confirm.Audit);
        var code = r switch { ConfirmResult.Expired => "confirmation_expired", ConfirmResult.Mismatch => "confirmation_mismatch", ConfirmResult.Required => "confirm_required", ConfirmResult.TypedMismatch => "typed_mismatch", _ => "confirmation_invalid" };
        await AuditAsync(audit, log, x.Id.UserId, x.Connection, action, path, "denied", code);
        return (Fail(code), null);
    }

    /// <summary>
    /// Der Eintrag "started" VOR dem ersten schreibenden Schritt, mit Stufe und Modus ("dangerous:check", nie ein Inhalt). Laesst er sich nicht schreiben, wird nichts ausgefuehrt (wie bei den Host-Aktionen); das Ergebnis kommt am Ende als eigener Eintrag.
    /// </summary>
    private static async Task<IResult?> StartedAsync(FileAuditStore audit, ILogger log, Ctx x, string action, string? path, int? items = null, string? host = null, string? level = null)
    {
        try { await audit.AddAsync(x.Id.UserId, x.Connection.Id, host ?? HostName(x.Connection), action, path, "started", level, items, CancellationToken.None); return null; }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            log.LogError("Verlauf nicht geschrieben ({Type}): Aktion {Action} nicht ausgefuehrt", e.GetType().Name, action);
            return Fail("audit_failed");
        }
    }

    private static bool TryEnterStream(Guid user)
    {
        var ok = false;
        Streams.AddOrUpdate(user, _ => { ok = true; return 1; }, (_, n) => { if (n < MaxStreams) { ok = true; return n + 1; } return n; });
        return ok;
    }

    private static void LeaveStream(Guid user) => Streams.AddOrUpdate(user, 0, (_, n) => Math.Max(0, n - 1));

    /// <summary>Mehrere geliehene Sitzungen, die zusammen zurueckgegeben werden.</summary>
    private sealed class Leases : IAsyncDisposable
    {
        private readonly List<SftpPool.Lease> _all = [];
        public async Task<SftpPool.Lease> AddAsync(Task<SftpPool.Lease> lease) { var l = await lease; _all.Add(l); return l; }
        public async ValueTask DisposeAsync() { foreach (var l in _all) await l.DisposeAsync(); }
    }
}
