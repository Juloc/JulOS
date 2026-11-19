using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace JulOS.Backend.Logic;

/// <summary>
/// Quick Connect: eine Sitzung ohne gespeicherte Verbindung. Nichts davon kommt in die Datenbank; das Passwort gilt nur fuer diese eine Sitzung.
/// Die Regeln fuer Adresse, Art, Port und Benutzer sind dieselben wie bei gespeicherten Verbindungen (ConnectionRules), die Erlaubnisliste gilt ebenso.
/// </summary>
public static class QuickConnect
{
    /// <summary>Hoechstens so viele neue Quick-Connect-Tickets je Benutzer und Minute (sonst waere es ein Werkzeug zum Abklopfen von Zielen).</summary>
    public const int MaxPerMinute = 12;

    /// <summary>
    /// Der Slot-Schluessel einer Quick-Sitzung: stabil je Benutzerziel (Art, Adresse, Port, Benutzername), aber nie die Kennung einer gespeicherten Verbindung.
    /// So bekommt dasselbe Ziel denselben Slot wie bei gespeicherten Verbindungen (eine Sitzung loest die andere am gleichen Slot ab).
    /// </summary>
    public static Guid IdOf(string kind, string host, int port, string username)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"quick\n{kind}\n{host.ToLowerInvariant()}\n{port}\n{username}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// Merkt Host-Schluessel (SSH) und Zertifikat-Fingerabdruecke (RDP) der Quick-Ziele nur im Speicher, solange der Server laeuft: ein Ziel, das sich
/// zwischendurch aendert, wird so trotzdem bemerkt, ohne dass etwas gespeichert wird. Begrenzt, damit es nicht unbegrenzt waechst.
/// </summary>
public sealed class QuickTrust
{
    public const int MaxEntries = 500;
    private readonly ConcurrentDictionary<string, string> _seen = new();

    private static string KeyOf(Guid user, string kind, string host, int port) => $"{user}|{kind}|{host.ToLowerInvariant()}|{port}";

    public string? Get(Guid user, string kind, string host, int port) => _seen.TryGetValue(KeyOf(user, kind, host, port), out var v) ? v : null;

    public void Set(Guid user, string kind, string host, int port, string value)
    {
        if (_seen.Count >= MaxEntries) _seen.Clear();
        _seen[KeyOf(user, kind, host, port)] = value;
    }
}
