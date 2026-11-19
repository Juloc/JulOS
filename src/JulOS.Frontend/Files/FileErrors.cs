using JulOS.Integrations.Files;
using JulOS.Integrations.Ssh;

namespace JulOS.Frontend.Files;

/// <summary>Ein Host-Schluessel, der nicht ohne ausdrueckliche Bestaetigung gilt: neu (noch keiner gemerkt) oder geaendert.</summary>
internal sealed class HostKeyProblem(bool changed, SshHostKey presented, string? known) : Exception(changed ? "Der Host-Schlüssel hat sich geändert." : "Der Host-Schlüssel ist noch nicht bestätigt.")
{
    public bool Changed { get; } = changed;
    public SshHostKey Presented { get; } = presented;
    public string? Known { get; } = known;
}

/// <summary>Fuer die Verbindung gibt es kein gespeichertes Passwort und keine offene Sitzung: die Seite muss es abfragen.</summary>
internal sealed class PasswordRequiredException(Guid connection) : Exception("Passwort fehlt.")
{
    public Guid Connection { get; } = connection;
}

/// <summary>Die festen Fehlercodes der Dateien-App und ihre Status und deutschen Rueckfalltexte (die Seite uebersetzt den Code; der Text steht nur fuer Programme ohne Uebersetzung).</summary>
internal static class FileErrors
{
    private static readonly Dictionary<string, (int Status, string Text)> Table = new()
    {
        ["not_found"] = (404, "Pfad nicht gefunden."),
        ["denied"] = (403, "Zugriff am Ziel verweigert."),
        ["exists"] = (409, "Es gibt diesen Pfad schon."),
        ["not_empty"] = (409, "Der Ordner ist nicht leer."),
        ["not_a_directory"] = (400, "Das ist kein Ordner."),
        ["connection_lost"] = (502, "Die Verbindung zum Ziel ist abgerissen."),
        ["failed"] = (502, "Die Operation am Ziel ist fehlgeschlagen."),
        ["too_many_items"] = (413, "Zu viele Einträge für diese Operation."),
        ["too_deep"] = (413, "Der Ordner ist zu tief verschachtelt."),
        ["too_large"] = (413, "Die Datei ist zu groß."),
        ["into_itself"] = (400, "Ein Ordner lässt sich nicht in sich selbst kopieren oder verschieben."),
        ["same_place"] = (400, "Quelle und Ziel sind derselbe Ordner."),
        ["type_mismatch"] = (409, "Am Ziel gibt es eine Datei und einen Ordner mit demselben Namen."),
        ["target_is_link"] = (409, "Am Ziel gibt es einen Link mit diesem Namen; ein Link wird nie ersetzt."),
        ["root_forbidden"] = (400, "Das Wurzelverzeichnis wird nie gelöscht."),
        ["invalid_name"] = (400, "Ungültiger Name."),
        ["invalid_path"] = (400, "Ungültiger Pfad."),
        ["invalid_request"] = (400, "Ungültige Anfrage."),
        ["not_text"] = (415, "Das ist keine Textdatei."),
        ["write_off"] = (403, "Schreiben ist auf diesem Server nicht eingeschaltet (JULOS_FILES_WRITE=on)."),
        ["confirm_required"] = (400, "Die Bestätigung fehlt."),
        ["connection_not_found"] = (404, "Verbindung nicht gefunden."),
        ["not_ssh"] = (400, "Diese Verbindung ist keine SSH-Verbindung."),
        ["target_not_allowed"] = (403, "Dieses Ziel ist nicht erlaubt."),
        ["password_required"] = (409, "Das Passwort fehlt."),
        ["password_unreadable"] = (409, "Das gespeicherte Passwort lässt sich nicht lesen."),
        ["logon_failed"] = (422, "Anmeldung abgelehnt."),
        ["unreachable"] = (502, "Das Ziel ist nicht erreichbar."),
        ["timeout"] = (504, "Zeitüberschreitung beim Verbinden."),
        ["sftp_unavailable"] = (502, "SFTP ist auf dem Ziel nicht verfügbar."),
        ["host_key_new"] = (409, "Der Host-Schlüssel ist noch nicht bestätigt."),
        ["host_key_changed"] = (409, "Der Host-Schlüssel hat sich geändert."),
        ["too_many_sessions"] = (429, "Zu viele offene Verbindungen."),
        ["rate_limited"] = (429, "Zu viele Anfragen, bitte kurz warten."),
        ["busy"] = (429, "Es laufen schon mehrere Übertragungen oder Aufträge."),
        ["job_not_found"] = (404, "Auftrag nicht gefunden."),
        ["cancelled"] = (499, "Abgebrochen."),
    };

    public static (int Status, string Text) Describe(string code) => Table.TryGetValue(code, out var v) ? v : Table["failed"];

    /// <summary>Der Code zu einer Ausnahme (nie der Text der Ausnahme: der kann Pfade oder Namen der Bibliothek enthalten).</summary>
    public static string Code(Exception e) => e switch
    {
        FileRuleException r => Table.ContainsKey(r.Code) ? r.Code : "failed",
        RemoteFileException f => f.Error switch
        {
            RemoteFileError.NotFound => "not_found", RemoteFileError.Denied => "denied", RemoteFileError.Exists => "exists", RemoteFileError.NotEmpty => "not_empty",
            RemoteFileError.NotADirectory => "not_a_directory", RemoteFileError.Closed => "connection_lost", _ => "failed",
        },
        HostKeyProblem h => h.Changed ? "host_key_changed" : "host_key_new",
        PasswordRequiredException => "password_required",
        PoolFullException => "too_many_sessions",
        SshException s => s.Failure switch
        {
            SshFailure.LogonFailed => "logon_failed", SshFailure.Unreachable => "unreachable", SshFailure.Timeout => "timeout", SshFailure.HostKeyRejected => "host_key_changed",
            _ => s.InnerException?.Message.Contains("ubsystem", StringComparison.OrdinalIgnoreCase) == true ? "sftp_unavailable" : "failed",   // "Subsystem 'sftp' could not be executed"
        },
        OperationCanceledException => "cancelled",
        IOException or InvalidOperationException => "connection_lost",
        _ => "failed",
    };
}
