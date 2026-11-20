namespace JulOS.Frontend.Files;

// Alle Typen der Dateien-App fuer SFTP (Anfragen und Antworten); die JSON-Namen sind camelCase, siehe docs/FILES.md.
internal sealed record FileErrorBody(string Error, string Code, string? Fingerprint = null, string? Algorithm = null, string? Known = null, Guid? Connection = null);
/// <summary>Local: gibt es den Ort "Gemeinsamer Ordner"; Write: JULOS_FILES_WRITE=on; MaxBytes: Obergrenze je Datei und Operation.</summary>
internal sealed record FilesInfoDto(bool Local, bool Write, long MaxBytes);
/// <summary>Kind: file, directory, link, other. Resolved: wohin ein Link zeigt (file, directory, other; leer bei kaputtem oder ungeprueftem Link). Openable: Ordner oder Link auf einen Ordner.</summary>
internal sealed record RemoteEntryDto(string Name, string Path, string Kind, long Size, DateTimeOffset Modified, string Mode, string? Resolved, bool Openable);
internal sealed record RemoteListingDto(string Path, RemoteEntryDto[] Entries, bool Truncated, int Hidden);
internal sealed record TextDto(string Path, string Text, bool Truncated, long Size);
internal sealed record OpenRequest(Guid? Connection, string? Password, string? AcceptHostKey);
/// <summary>HostKey: known (wie gemerkt), remembered (erstmals bestaetigt und gemerkt), replaced (geaenderten Schluessel bestaetigt).</summary>
internal sealed record OpenResponse(string Home, string HostKey, string? Fingerprint, string? Algorithm);
internal sealed record CloseRequest(Guid? Connection);
internal sealed record MkdirRequest(Guid? Connection, string? Dir, string? Name, string? Token);
internal sealed record RenameRequest(Guid? Connection, string? Path, string? Name, bool? Overwrite, string? Token);
internal sealed record FileTarget(Guid? Connection, string? Dir);
/// <summary>
/// Schritt 1 jeder schreibenden Aktion. Action: upload, mkdir, rename, delete, copy, move. Je nach Aktion: Paths (Quellen), Dir (Ordner: Ziel beim Hochladen, Mkdir und Kopieren), Name (neuer Name), Names (Dateinamen
/// beim Hochladen), Target (Ziel von copy und move).
/// </summary>
internal sealed record PrepareRequest(string? Action, Guid? Connection, string[]? Paths, string? Dir, string? Name, string[]? Names, FileTarget? Target);
/// <summary>
/// Die Bestaetigung: Token (60 s, einmal; im Server nur sein SHA-256) und was genau geschaehe (so wie der Server es geprueft hat: Host, Ziel-Host, Pfade, Anzahl, Groesse, Namen, die ersetzt wuerden, ein Ordner mit Inhalt).
/// </summary>
internal sealed record PrepareResponse(string Token, int ExpiresIn, string Action, string Host, string? TargetHost, string[] Paths, string? Dir, string? Name, string[]? Names, int Files, int Directories, int Links, long Bytes, string[] Conflicts, bool NonEmpty);
internal sealed record DeleteRequest(Guid? Connection, string[]? Paths, bool? Recursive, string? Token);
internal sealed record CopyRequest(Guid? Connection, string[]? Paths, FileTarget? Target, bool? Move, bool? Overwrite, string? Token);
internal sealed record JobStartedDto(Guid Job);
internal sealed record JobResultDto(int Files, int Directories);
/// <summary>State: running, done, failed, cancelled. Error: Code bei failed.</summary>
internal sealed record JobDto(Guid Id, string Kind, string State, string? Error, int Done, int Total, long Bytes, long BytesTotal, int Skipped, int SkippedExisting, string? Current, JobResultDto? Result);
internal sealed record UploadedDto(string Path, long Bytes, bool Replaced);
internal sealed record PathDto(string Path);
internal sealed record FileAuditEntryDto(DateTimeOffset At, string Host, string Action, string? Path, string Result, string? Detail, int? Items);
internal sealed record FileAuditDto(FileAuditEntryDto[] Entries);
