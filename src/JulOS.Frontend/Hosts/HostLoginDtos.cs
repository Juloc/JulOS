namespace JulOS.Frontend.Hosts;

/// <summary>Was eine Verbindung des Hosts beim Verbinden benutzt. Source: host (der Host-Login) oder own (ihr eigener); AddressNew: der Host-Login hat ein Passwort, das fuer die Adresse dieser Verbindung nicht gilt.</summary>
internal sealed record HostLoginConnectionDto(Guid Id, string Name, string Kind, string Address, string Username, string Source, bool OwnLogin, bool AddressNew);

/// <summary>
/// Der Login eines Hosts (docs/HOSTS.md): User (null: keiner), HasPassword (nie das Passwort), CanStore (der Server hat einen Schluessel), Suggestion (ein Benutzername aus den Verbindungen, wenn es noch keinen Login gibt),
/// Connections (jede Verbindung des Hosts und was sie benutzt), Pinned (nur nach dem Setzen: so viele Verbindungen behalten ihren eigenen Benutzer, weil er ein anderer ist).
/// </summary>
internal sealed record HostLoginDto(string? User, bool HasPassword, bool CanStore, string? Suggestion, HostLoginConnectionDto[] Connections, int Pinned = 0);

/// <summary>User: der Benutzername; Password: ein neues Passwort (leer: das gespeicherte bleibt); ForgetPassword: nur der Benutzer, das gespeicherte Passwort entfaellt.</summary>
internal sealed record HostLoginRequest(string? User, string? Password = null, bool? ForgetPassword = null);
