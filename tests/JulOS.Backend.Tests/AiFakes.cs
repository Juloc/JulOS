using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace JulOS.Backend.Tests;

/// <summary>
/// Ein Stand-in fuer das Programm `codex` (ein bash-Skript, kein Netz): antwortet auf `--version`, `login status`, `login --device-auth`, `logout` und `app-server` (JSON-Zeilen) so, wie die
/// Projekte des Eigentuemers es vom echten erwarten. Es schreibt auf, wie es aufgerufen wurde (calls.log, env.txt, cwd.txt). Stichworte in der Nachricht: CRASH, FAIL, LIMIT, SLOW, ASKME.
/// Die Anmeldung gilt, wenn CODEX_HOME/signed-in existiert; die Geraete-Anmeldung wartet auf die Datei "approve" im Aufzeichnungsordner.
/// </summary>
internal sealed class FakeCodexProgram : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "julos-fakecodex-" + Guid.NewGuid().ToString("N"));
    public string Executable => Path.Combine(Folder, "codex");
    public string Record => Path.Combine(Folder, "record");
    public string DataDir => Path.Combine(Folder, "data");

    public FakeCodexProgram(string url = "https://auth.openai.com/codex/device", bool signedIn = true)
    {
        Directory.CreateDirectory(Record);
        File.WriteAllText(Executable, Script.Replace("{{REC}}", Record, StringComparison.Ordinal).Replace("{{URL}}", url, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (signedIn) SignIn();
    }

    public string Home => Path.Combine(DataDir, "ai", "codex", "home");
    public string Work => Path.Combine(DataDir, "ai", "codex", "work");
    public void SignIn() { Directory.CreateDirectory(Home); File.WriteAllText(Path.Combine(Home, "signed-in"), ""); }
    public void Approve() => File.WriteAllText(Path.Combine(Record, "approve"), "");
    public string[] Calls => File.Exists(Path.Combine(Record, "calls.log")) ? File.ReadAllLines(Path.Combine(Record, "calls.log")) : [];
    public string Read(string name) => File.ReadAllText(Path.Combine(Record, name));
    public bool Has(string name) => File.Exists(Path.Combine(Record, name));
    public void Dispose() { try { Directory.Delete(Folder, recursive: true); } catch (IOException) { } }

    private const string Script = """
        #!/usr/bin/env bash
        rec='{{REC}}'
        echo "$*" >> "$rec/calls.log"
        echo "$CODEX_HOME" > "$rec/home.txt"; env | sort > "$rec/env.txt"; pwd > "$rec/cwd.txt"
        case "$1" in
          --version) echo "codex-cli 0.0.0-fake"; exit 0;;
          logout) rm -f "$CODEX_HOME/signed-in"; exit 0;;
          login)
            if [ "$2" = "status" ]; then
              if [ -f "$CODEX_HOME/signed-in" ]; then echo "Logged in using ChatGPT"; exit 0; else echo "Not logged in" >&2; exit 1; fi
            fi
            printf '\033[1mOpen this link\033[0m\n   \033[36m{{URL}}\033[0m\n\nEnter this one-time code\n   \033[1mABCD-12345\033[0m\n'
            for i in $(seq 1 150); do
              if [ -f "$rec/approve" ]; then mkdir -p "$CODEX_HOME"; touch "$CODEX_HOME/signed-in"; exit 0; fi
              sleep 0.1
            done
            exit 1;;
          app-server)
            n=0
            while IFS= read -r line; do
              id=$(printf '%s' "$line" | sed -n 's/^{"id":\([0-9]*\),.*/\1/p')
              case "$line" in
                *'"method":"initialize"'*) echo "{\"id\":$id,\"result\":{}}";;
                *'"method":"model/list"'*) echo "{\"id\":$id,\"result\":{\"data\":[{\"model\":\"fake-1\",\"displayName\":\"Fake 1\",\"supportedReasoningEfforts\":[{\"reasoningEffort\":\"low\"},{\"reasoningEffort\":\"high\"}],\"defaultReasoningEffort\":\"low\"}]}}";;
                *'"method":"account/rateLimits/read"'*) echo "{\"id\":$id,\"result\":{\"rateLimits\":{\"planType\":\"plus\",\"primary\":{\"usedPercent\":12.5,\"windowDurationMins\":300,\"resetsAt\":1900000000}}}}";;
                *'"method":"thread/start"'*)
                  n=$((n+1)); echo "$line" > "$rec/thread-start-$n.json"
                  echo "{\"id\":$id,\"result\":{\"thread\":{\"id\":\"thr_$n\"}}}";;
                *'"method":"turn/start"'*)
                  thread=$(printf '%s' "$line" | sed -n 's/.*"threadId":"\([^"]*\)".*/\1/p')
                  echo "$line" > "$rec/turn-start.json"
                  echo "{\"id\":$id,\"result\":{\"turn\":{\"id\":\"turn_1\"}}}"
                  case "$line" in
                    *CRASH*) exit 3;;
                    *FAIL*)
                      echo "{\"method\":\"error\",\"params\":{\"threadId\":\"$thread\",\"error\":{\"message\":\"boom secret-token-0123456789abcdef0123456789abcdef\"}}}"
                      echo "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"$thread\",\"turn\":{\"id\":\"turn_1\",\"status\":\"failed\"}}}";;
                    *LIMIT*)
                      echo "{\"method\":\"error\",\"params\":{\"threadId\":\"$thread\",\"error\":{\"message\":\"You've hit your usage limit. Try again later.\"}}}"
                      echo "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"$thread\",\"turn\":{\"id\":\"turn_1\",\"status\":\"failed\"}}}";;
                    *SLOW*)
                      echo "{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"$thread\",\"turnId\":\"turn_1\",\"delta\":\"Hi\"}}"
                      while IFS= read -r l2; do
                        case "$l2" in
                          *'"method":"turn/interrupt"'*)
                            i2=$(printf '%s' "$l2" | sed -n 's/^{"id":\([0-9]*\),.*/\1/p')
                            touch "$rec/interrupted"
                            echo "{\"id\":$i2,\"result\":{}}"
                            echo "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"$thread\",\"turn\":{\"id\":\"turn_1\",\"status\":\"interrupted\"}}}"
                            break;;
                        esac
                      done;;
                    *)
                      case "$line" in
                        *ASKME*)
                          echo "{\"id\":900,\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"threadId\":\"$thread\",\"command\":\"rm -rf /\"}}"
                          IFS= read -r reply; echo "$reply" > "$rec/reply.txt";;
                      esac
                      echo "{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"$thread\",\"turnId\":\"turn_1\",\"delta\":\"Hello\"}}"
                      echo "{\"method\":\"item/agentMessage/delta\",\"params\":{\"threadId\":\"$thread\",\"turnId\":\"turn_1\",\"delta\":\" world\"}}"
                      echo "{\"method\":\"thread/tokenUsage/updated\",\"params\":{\"threadId\":\"$thread\",\"tokenUsage\":{\"last\":{\"inputTokens\":11,\"outputTokens\":7}}}}"
                      echo "{\"method\":\"turn/completed\",\"params\":{\"threadId\":\"$thread\",\"turn\":{\"id\":\"turn_1\",\"status\":\"completed\"}}}";;
                  esac;;
              esac
            done;;
        esac
        """;

    /// <summary>Ein tar.gz mit den gegebenen Dateien (Name, Inhalt), wie das Archiv einer Veroeffentlichung.</summary>
    public static byte[] Archive(params (string Name, string Content)[] files)
    {
        using var raw = new MemoryStream();
        using (var gz = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var (name, content) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        return raw.ToArray();
    }
}

/// <summary>Ein HttpMessageHandler, der jede Anfrage mit einer fertigen Antwort beantwortet und sie sich merkt (kein Netz).</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri? Uri, HttpMethod Method, Dictionary<string, string> Headers, string Body)> Seen { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Seen) Seen.Add((request.RequestUri, request.Method, headers, body));
        return respond(request);
    }

    public static HttpResponseMessage Sse(string body) => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    public static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
