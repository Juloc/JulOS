using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace JulOS.Frontend.Agent;

/// <summary>Die Agent-Datei, die dieser Server mitliefert: ihre Version (aus dem Skript selbst) und ihre SHA-256-Summe.</summary>
public sealed record AgentFile(string Version, string Sha256, long Size);

/// <summary>
/// Kennt die "neueste" Agent-Version: die Datei <c>agent-files/julos-agent.py</c> neben dem Server (im Image mitgeliefert). Version und Summe werden aus den
/// Bytes der Datei gelesen und gemerkt, bis sich die Datei aendert; so kann die Anweisung an den Agenten nie von dem abweichen, was <c>/agent-files/julos-agent.py</c> ausliefert.
/// </summary>
public sealed partial class AgentRelease(string? directory = null)
{
    /// <summary>Der feste Pfad, von dem sich ein Agent aktualisiert: immer am Server, mit dem er verbunden ist (der Agent setzt die Adresse selbst davor).</summary>
    public const string Path = "/agent-files/julos-agent.py";

    /// <summary>Der Ordner mit den Agent-Dateien (die Auslieferung unter /agent-files/ liest ebenfalls von hier).</summary>
    public string Directory { get; } = directory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "agent-files");

    private string _file => System.IO.Path.Combine(Directory, "julos-agent.py");
    private readonly Lock _lock = new();
    private (DateTime Written, long Length, AgentFile? Info)? _cache;

    [GeneratedRegex("""^AGENT_VERSION\s*=\s*"(\d+\.\d+\.\d+)"\s*(#.*)?$""", RegexOptions.Multiline)]
    private static partial Regex VersionLine();

    /// <summary>Die Dateien, die der Server fuer die Ziel-Maschine ausliefert (GET /agent-files/&lt;Name&gt;); nichts anderes aus dem Ordner.</summary>
    public static readonly string[] Files = ["julos-agent.py", "julos-agent-helper.py", "julos-clip.py", "install-agent.sh", "install-linux-agent.sh", "install-host-agent.sh"];

    /// <summary>Der Name der Pruefsummen-Datei (<c>sha256sum</c>-Format), die der Host-Installer gegen seinen Download prueft.</summary>
    public const string SumsName = "SHA256SUMS";

    private readonly Dictionary<string, (DateTime Written, long Length, string Hash)> _hashes = [];

    /// <summary>Die Pruefsummen der ausgelieferten Dateien, eine Zeile je Datei ("&lt;sha256&gt;  &lt;Name&gt;"); Dateien, die fehlen, stehen nicht darin. Aus den Bytes gelesen, also immer das, was /agent-files/ ausliefert.</summary>
    public string Sums()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var name in Files)
        {
            var path = System.IO.Path.Combine(Directory, name);
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; }
            catch (IOException) { continue; }
            string hash;
            lock (_lock)
            {
                if (_hashes.TryGetValue(name, out var c) && c.Written == info.LastWriteTimeUtc && c.Length == info.Length) hash = c.Hash;
                else
                {
                    try { hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))); }
                    catch (IOException) { continue; }
                    _hashes[name] = (info.LastWriteTimeUtc, info.Length, hash);
                }
            }
            sb.Append(hash).Append("  ").Append(name).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Die mitgelieferte Agent-Datei; null, wenn sie fehlt oder keine Version nennt.</summary>
    public AgentFile? Latest()
    {
        FileInfo info;
        try { info = new FileInfo(_file); if (!info.Exists) return null; }
        catch (IOException) { return null; }
        lock (_lock)
        {
            if (_cache is { } c && c.Written == info.LastWriteTimeUtc && c.Length == info.Length) return c.Info;
            AgentFile? read = null;
            try
            {
                var bytes = File.ReadAllBytes(_file);
                if (VersionLine().Match(System.Text.Encoding.UTF8.GetString(bytes)) is { Success: true } m)
                    read = new AgentFile(m.Groups[1].Value, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length);
            }
            catch (IOException) { return null; }
            _cache = (info.LastWriteTimeUtc, info.Length, read);
            return read;
        }
    }
}
