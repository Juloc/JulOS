namespace JulOS.Frontend.Auth;

internal sealed record Health(bool Ok, bool Db, string Version);
internal sealed record ErrorBody(string Error, int? RetryAfter = null, string? Code = null);
internal sealed record StepUpRequest(string? Code);
internal sealed record StepUpResponse(DateTimeOffset? Until, int Minutes);
internal sealed record SetupStatus(bool Needed);
internal sealed record SetupBeginRequest(string? Code, string? User, string? Password);
internal sealed record SetupBeginResponse(string Secret, string Uri, string[] Recovery);
internal sealed record SetupFinishRequest(string? Code, string? User, string? Totp);
internal sealed record LoginRequest(string? User, string? Password, string? Code);
internal sealed record LoginResponse(string User, int RecoveryLeft);
internal sealed record PasswordChangeRequest(string? Current, string? Next);
internal sealed record TotpBeginRequest(string? Current);
internal sealed record TotpBeginResponse(string Secret, string Uri);
internal sealed record TotpFinishRequest(string? Code);
internal sealed record TotpFinishResponse(string[] Recovery);
internal sealed record MeResponse(string? User, bool SetupNeeded);
internal sealed record SessionDto(bool Current, DateTimeOffset CreatedAt, DateTimeOffset LastSeen, string? UserAgent, string? Ip);
internal sealed record SessionsResponse(SessionDto[] Sessions);
internal sealed record EndedResponse(int Ended);
internal sealed record ActivateRequest(bool? Maximize);
internal sealed record LaunchRequest(string? Id);
internal sealed record ModeRequest(string? Mode);
internal sealed record UploadResponse(bool Ok);
/// <summary>Eine gespeicherte Verbindung. HasPassword: sie hat ein eigenes Passwort. OwnLogin: sie verlangt ihren eigenen Login. LoginSource (host oder own), LoginUser, LoginPassword (liegt ein Passwort bereit, ohne zu fragen) und LoginAddressNew (der Host-Login gilt nicht fuer diese Adresse) sagen, was beim Verbinden gilt; nie ein Passwort.</summary>
internal sealed record ConnectionDto(Guid Id, string Name, string Kind, string Host, int Port, string Username, bool HasPassword, Guid HostId, bool DriveShare = false, bool DriveWrite = false, string Domain = "", string Notes = "", string Layout = "de",
    string Program = "shell", string WorkDir = "", bool KeepAlive = false, string CustomCommand = "",
    bool OwnLogin = false, string LoginSource = "own", string LoginUser = "", bool LoginPassword = false, bool LoginAddressNew = false);
internal sealed record ConnectionsResponse(ConnectionDto[] Connections, bool CanStorePasswords, bool DriveAvailable = false);
internal sealed record ConnectionRequest(string? Name, string? Kind, string? Host, int? Port, string? Username, string? Password = null, bool? ForgetPassword = null, bool? DriveShare = null, bool? DriveWrite = null, Guid? HostId = null, string? Domain = null, string? Notes = null, string? Layout = null,
    string? Program = null, string? WorkDir = null, bool? KeepAlive = null, string? CustomCommand = null, bool? OwnLogin = null);
/// <summary>Eine Kachel "Claude auf ..." oder "Codex auf ..." aus einer vorhandenen Terminal-Verbindung (POST /api/connections/{id}/program): Programm, Arbeitsverzeichnis, tmux und ein Name (ohne Name: "Claude auf <Host>").</summary>
internal sealed record ProgramTileRequest(string? Program, string? WorkDir = null, bool? KeepAlive = null, string? Name = null);
/// <summary>Username und Domain: Anmeldedaten fuer genau diesen Verbindungsversuch (Anmeldedialog in der Sitzung); sie ersetzen die der Verbindung und werden nicht gespeichert.</summary>
internal sealed record TicketRequest(Guid? Connection, string? Password, int? Width, int? Height, string? Sid = null, int? Cols = null, int? Rows = null, string? AcceptHostKey = null, string? AcceptCert = null, QuickTarget? Quick = null, string? Username = null, string? Domain = null);
/// <summary>Quick Connect: Ziel einer Sitzung ohne gespeicherte Verbindung (das Passwort steht im Ticket selbst und wird nirgends abgelegt).</summary>
internal sealed record QuickTarget(string? Kind, string? Host, int? Port, string? Username);
internal sealed record TicketResponse(string Ticket, string Kind, int Width, int Height, string Engine = "stream", string? Drive = null, string? Layout = null);
