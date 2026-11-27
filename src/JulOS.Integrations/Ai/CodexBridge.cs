using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace JulOS.Integrations.Ai;

public sealed class CodexBridgeOptions
{
    /// <summary>Warten nach einem Absturz, verdoppelt sich bei jedem weiteren, bis <see cref="BackoffMax"/>.</summary>
    public TimeSpan BackoffStart { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Frist fuer eine Steueranfrage an das Programm (Start, Modelle).</summary>
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Nur fuer Tests: ein anderes Programm als das geladene.</summary>
    public string? Executable { get; init; }
}

/// <summary>
/// Die Codex-Bruecke: ein `codex app-server` (JSON-Zeilen ueber stdio) fuer den ganzen Server. Er laeuft erst, wenn jemand ihn braucht, und wird nach einem Absturz mit wachsender
/// Wartezeit neu gestartet (beim naechsten Gebrauch, nicht in einer Dauerschleife). Das Programm bekommt keine Umgebung von JulOS (keine Datenbank, keine Schluessel), nur eigene
/// Ordner im Datenordner, und wird mit einer Argumentliste gestartet, nie ueber eine Shell. Jede Anfrage ist ein eigener, fluechtiger, nur lesender Faden ohne Werkzeuge; mehrere
/// laufen nebeneinander ueber denselben Prozess (die Ereignisse tragen die Faden-Kennung). Die Anmeldung ist die Geraete-Anmeldung des Programms.
/// Nicht gegen das echte Programm geprueft: die Form der Nachrichten folgt dem, was Jularr und andere Projekte benutzen, getestet ist sie gegen ein Fake-Programm.
/// </summary>
public sealed class CodexBridge : IAiProvider, IDisposable
{
    private readonly ICodexRunner _runner;
    private readonly CodexInstaller _installer;
    private readonly TimeProvider _time;

    /// <summary>Das Programm im selben Container (wie bisher).</summary>
    public CodexBridge(CodexInstaller installer, TimeProvider time, CodexBridgeOptions? options = null) : this(new LocalRunner(installer, options?.Executable), installer, time, options) { }

    /// <summary>Mit einem beliebigen Starter: im selben Container (LocalRunner) oder im eigenen (SocketRunner).</summary>
    public CodexBridge(ICodexRunner runner, CodexInstaller installer, TimeProvider time, CodexBridgeOptions? options = null)
    {
        _runner = runner; _installer = installer; _time = time; _options = options ?? new();
    }

    private const string BaseInstructions = "You are a helpful assistant. Answer the user's request directly.";
    private static readonly string[] OffFeatures =
    [
        "apps", "browser_use", "browser_use_external", "computer_use", "goals", "image_generation", "multi_agent", "plugins", "hooks", "memories", "code_mode_host", "shell_tool", "unified_exec",
        "view_image", "skill_search", "sleep_tool", "tool_suggest", "collaboration_modes", "in_app_browser", "mentions_v2", "workspace_dependencies", "worktrees", "tool_call_mcp_elicitation",
        "write_stdin_approval",
    ];

    private readonly CodexBridgeOptions _options;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly object _loginLock = new();
    private Connection? _connection;
    private int _failures;
    private DateTimeOffset _nextStart;
    private CodexDeviceLogin? _login;
    private (DateTimeOffset At, bool SignedIn)? _signedInCache;
    private readonly SemaphoreSlim _statusLock = new(1, 1);

    public string Id => "codex";

    public ICodexRunner Runner => _runner;

    public bool Running => _connection is { Dead: false };

    // ---------------------------------------------------------------- Zustand und Anmeldung

    public async Task<AiProviderState> GetStateAsync(CancellationToken ct)
    {
        if (_runner.Isolated)
        {
            await _runner.RefreshAsync(ct);
            if (!_runner.Available) return new AiProviderState("error", "sidecar_unreachable");
        }
        else if (_options.Executable is null)
        {
            if (!_installer.Supported) return new AiProviderState("error", "unsupported_platform");
            if (_installer.IsDownloading) return new AiProviderState("downloading");
            if (!_installer.IsInstalled) return _installer.Failure is { } f ? new AiProviderState("install_failed", f) : new AiProviderState("not_installed");
            if (!_runner.Available) return new AiProviderState("install_failed", _installer.Failure);
        }
        if (LoginState().State is "starting" or "waiting") return new AiProviderState("login_waiting");
        return await IsSignedInAsync(ct) ? new AiProviderState("ready") : new AiProviderState("not_signed_in");
    }

    // Einzelflug: viele gleichzeitige Fragen starten nicht je ein Programm; wer wartet, sieht danach den Stand des ersten (5 Sekunden gemerkt)
    private async Task<bool> IsSignedInAsync(CancellationToken ct)
    {
        if (FreshStatus() is { } known) return known;
        await _statusLock.WaitAsync(ct);
        try
        {
            if (FreshStatus() is { } again) return again;
            var result = await RunAsync(CodexCommand.LoginStatus, ct);
            var signedIn = result is { ExitCode: 0 } && result.Output.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
            _signedInCache = (_time.GetUtcNow(), signedIn);
            return signedIn;
        }
        finally { _statusLock.Release(); }
    }

    private bool? FreshStatus() => _signedInCache is { } c && _time.GetUtcNow() - c.At < TimeSpan.FromSeconds(5) ? c.SignedIn : null;

    public CodexLoginState LoginState() { lock (_loginLock) return _login?.Snapshot() ?? new CodexLoginState("none", null, null, null); }

    /// <summary>Startet die Geraete-Anmeldung (eine laufende wird weiterbenutzt).</summary>
    public CodexLoginState StartLogin()
    {
        lock (_loginLock)
        {
            if (_login?.Snapshot().State is "starting" or "waiting") return _login.Snapshot();
            _login?.Dispose();
            _login = CodexDeviceLogin.Start(_runner.Start(CodexCommand.Login), _time);
            var started = _login;
            _ = started.Completed.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || !t.Result) return;
                // Neue Anmeldung: der laufende Prozess kennt sie noch nicht
                _signedInCache = null; StopConnection();
            }, TaskScheduler.Default);
            return _login.Snapshot();
        }
    }

    public void CancelLogin() { lock (_loginLock) _login?.Cancel(); }

    public async Task LogoutAsync(CancellationToken ct)
    {
        CancelLogin();
        StopConnection();
        await RunAsync(CodexCommand.Logout, ct);
        _signedInCache = null;
    }

    /// <summary>Haelt den Prozess an (Schalter aus, Abmeldung, neue Anmeldung); der naechste Gebrauch startet ihn neu.</summary>
    public void Stop() { CancelLogin(); StopConnection(); }

    private void StopConnection()
    {
        var c = Interlocked.Exchange(ref _connection, null);
        c?.Kill(intentional: true);
    }

    public void Dispose() { Stop(); _login?.Dispose(); _startLock.Dispose(); _statusLock.Dispose(); }

    // ---------------------------------------------------------------- Modelle und Kontingent

    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct)
    {
        var c = await ConnectAsync(ct);
        var models = new List<AiModel>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var result = await c.RequestAsync("model/list", new JsonObject { ["cursor"] = cursor, ["limit"] = 100, ["includeHidden"] = false }, _options.ControlTimeout, ct);
            models.AddRange(CodexProtocol.ParseModels(result));
            cursor = CodexProtocol.Text(result, "nextCursor");
            if (cursor is null) break;
        }
        _failures = 0;
        return models;
    }

    /// <summary>Das Kontingent des ChatGPT-Tarifs, oder null, wenn diese Version oder dieses Konto es nicht nennt.</summary>
    public async Task<CodexPlanUsage?> GetPlanUsageAsync(CancellationToken ct)
    {
        var c = await ConnectAsync(ct);
        try { return CodexProtocol.ParseUsage(await c.RequestAsync("account/rateLimits/read", new JsonObject(), _options.ControlTimeout, ct)); }
        catch (AiProviderException e) when (e.Code == "provider_error") { return null; }
    }

    // ---------------------------------------------------------------- Anfrage

    /// <summary>Das Gespraech als Anweisung (alle system-Nachrichten) und Text (ein einzelner user-Text bleibt wie er ist, sonst mit Rollen als Verlauf).</summary>
    public static (string Instructions, string Prompt) Flatten(IReadOnlyList<AiMessage> messages)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Text));
        var rest = messages.Where(m => m.Role != "system").ToList();
        var prompt = rest.Count == 1 ? rest[0].Text : string.Join("\n\n", rest.Select(m => (m.Role == "assistant" ? "Assistant: " : "User: ") + m.Text));
        return (system.Length > 0 ? system : BaseInstructions, prompt);
    }

    public async IAsyncEnumerable<AiChunk> ChatAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (request.Messages.Any(m => m.Images is { Count: > 0 })) throw new AiProviderException("bad_request", "Die Codex-Brücke nimmt keine Bilder.");
        if (!await IsSignedInAsync(ct)) throw new AiProviderException("not_signed_in", "Das ChatGPT-Konto ist nicht angemeldet.");
        var c = await ConnectAsync(ct);
        var (instructions, prompt) = Flatten(request.Messages);

        var features = new JsonObject(); foreach (var f in OffFeatures) features[f] = false;
        var thread = await c.RequestAsync("thread/start", new JsonObject
        {
            ["model"] = request.Model, ["ephemeral"] = true, ["cwd"] = _runner.WorkDirectory, ["sandbox"] = "read-only", ["approvalPolicy"] = "never", ["baseInstructions"] = instructions,
            ["config"] = new JsonObject
            {
                ["features"] = features, ["web_search"] = "disabled", ["include_apply_patch_tool"] = false, ["include_permissions_instructions"] = false,
                ["include_environment_context"] = false, ["include_apps_instructions"] = false,
            },
        }, _options.ControlTimeout, ct);
        var threadId = thread.TryGetProperty("thread", out var created) ? CodexProtocol.Text(created, "id") : null;
        if (threadId is null) throw new AiProviderException("provider_error", "Codex hat keinen Faden begonnen.");
        // Fail-closed: was das Programm als wirksam meldet, muss "nur lesend, ohne Freigaben" sein; sonst keine Runde (und das Programm sieht keinen Text)
        if (!CodexProtocol.IsLockedDown(thread)) throw new AiProviderException("provider_error", "Codex hat nicht bestätigt, dass es nur lesend und ohne Freigaben läuft; die Anfrage wurde nicht gesendet.");

        var events = c.Open(threadId);
        string? turnId = null;
        var finished = false;
        try
        {
            var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt, ["text_elements"] = new JsonArray() });
            var turn = new JsonObject { ["threadId"] = threadId, ["input"] = input, ["model"] = request.Model };
            if (request.Effort is not null) turn["effort"] = request.Effort;
            // Die Antwort auf turn/start wird auch nach einem Abbruch abgewartet, damit die Runde danach mit ihrer Kennung unterbrochen werden kann (sonst rechnet sie weiter)
            var started = await c.RequestAsync("turn/start", turn, _options.ControlTimeout, CancellationToken.None);
            turnId = started.TryGetProperty("turn", out var t) ? CodexProtocol.Text(t, "id") : null;
            ct.ThrowIfCancellationRequested();

            AiUsage? usage = null; var any = false; string? failure = null;
            while (await events.WaitToReadAsync(ct))
            {
                while (events.TryRead(out var message))
                {
                    var root = message;
                    var parameters = root.TryGetProperty("params", out var p) ? p : default;
                    switch (CodexProtocol.Text(root, "method"))
                    {
                        case "item/agentMessage/delta" when CodexProtocol.Text(parameters, "delta") is { Length: > 0 } piece:
                            any = true; yield return new AiChunk(piece);
                            break;
                        case "thread/tokenUsage/updated" when parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tokenUsage", out var tu) && tu.TryGetProperty("last", out var last):
                            usage = new AiUsage(CodexProtocol.Number(last, "inputTokens"), CodexProtocol.Number(last, "outputTokens"));
                            break;
                        case "error" when parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("error", out var problem):
                            failure = CodexProtocol.Text(problem, "message");
                            if (CodexProtocol.PlanLimitIn(failure) is { } limit) throw limit;
                            break;
                        case "item/started" or "item/completed" when !CodexProtocol.IsPlainItem(parameters):
                            // Fail-closed: ein Werkzeug (Befehl, Datei, Suche ...) ist nie vorgesehen; die Runde wird abgebrochen (finally unten unterbricht sie)
                            throw new AiProviderException("provider_error", "Codex wollte ein Werkzeug benutzen; die Anfrage wurde abgebrochen.");
                        case "__ended":
                            throw new AiProviderException("provider_unavailable", "Das Codex-Programm wurde beendet.");
                        case "turn/completed":
                            var status = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("turn", out var ended) ? CodexProtocol.Text(ended, "status") : null;
                            if (status != "completed") throw CodexProtocol.PlanLimitIn(failure) ?? new AiProviderException("provider_error", AiText.Safe(failure) is { Length: > 0 } shown ? shown : "Codex hat nicht geantwortet.");
                            if (!any) throw new AiProviderException("provider_error", "Codex hat nichts geantwortet.");
                            finished = true; _failures = 0;
                            if (usage is not null) yield return new AiChunk(null, usage);
                            yield break;
                    }
                }
            }
            throw new AiProviderException("provider_unavailable", "Das Codex-Programm wurde beendet.");
        }
        finally
        {
            c.Close(threadId);
            // Ein Abbruch des Aufrufers bricht auch die Runde im Programm ab, sonst rechnet es weiter
            if (!finished && turnId is not null && !c.Dead) _ = c.InterruptAsync(threadId, turnId);
        }
    }

    // ---------------------------------------------------------------- Prozess

    private async Task<Connection> ConnectAsync(CancellationToken ct)
    {
        if (_connection is { Dead: false } live) return live;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_connection is { Dead: false } again) return again;
            if (_failures > 0 && _time.GetUtcNow() < _nextStart) throw new AiProviderException("provider_unavailable", "Das Codex-Programm startet nach einem Fehler neu; bitte gleich noch einmal.");
            Connection? c = null; var established = false;
            try
            {
                c = Connection.Start(_runner.Start(CodexCommand.AppServer), this);
                await c.RequestAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "julos", ["title"] = "JulOS", ["version"] = "1" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = false } }, _options.ControlTimeout, ct);
                await c.SendAsync(new JsonObject { ["method"] = "initialized", ["params"] = new JsonObject() }, ct);
                _connection = c; established = true;
                return c;
            }
            // Ein Abbruch des Aufrufers im Kaltstart zaehlt nicht als Fehler des Programms (sonst sperrte ein Schluessel-Inhaber die Bruecke mit Abbruechen); der Prozess wird trotzdem beendet (finally)
            catch (Exception e) when (e is AiProviderException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                NoteFailure();
                throw e as AiProviderException ?? new AiProviderException("provider_unavailable", "Das Codex-Programm lässt sich nicht starten.");
            }
            finally { if (!established) c?.Kill(intentional: true); }
        }
        finally { _startLock.Release(); }
    }

    private void NoteFailure()
    {
        var n = Interlocked.Increment(ref _failures);
        var wait = TimeSpan.FromTicks(Math.Min(_options.BackoffMax.Ticks, _options.BackoffStart.Ticks << Math.Min(n - 1, 20)));
        _nextStart = _time.GetUtcNow() + wait;
    }

    /// <summary>Ein kurzes Kommando des Programms (login status, logout) mit Frist; Ausgabe und Fehlerausgabe zusammen. Null, wenn es sich nicht starten laesst.</summary>
    private async Task<CommandResult?> RunAsync(CodexCommand command, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        ICodexProcess? process = null;
        try
        {
            process = _runner.Start(command);
            process.StandardInput.Close();
            using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8);
            var output = reader.ReadToEndAsync(limit.Token);
            int code;
            try { code = await process.WaitForExitAsync(limit.Token); }
            catch { process.Kill(); throw; }
            return new CommandResult(code, await output);
        }
        catch (Exception e) when (e is AiProviderException or IOException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { process?.Dispose(); }
    }

    private sealed record CommandResult(int ExitCode, string Output);

    /// <summary>Ein laufender app-server: Schreiben, Lesen und Verteilen der Antworten und Ereignisse.</summary>
    private sealed class Connection
    {
        private readonly ICodexProcess _process;
        private readonly StreamWriter _input;
        private readonly CodexBridge _owner;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly object _lock = new();
        private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = [];
        private readonly Dictionary<string, Channel<JsonElement>> _threads = [];
        private long _nextId;
        private volatile bool _intentional;

        private Connection(ICodexProcess process, CodexBridge owner) { _process = process; _input = new StreamWriter(process.StandardInput, new UTF8Encoding(false)); _owner = owner; }

        public volatile bool Dead;

        public static Connection Start(ICodexProcess process, CodexBridge owner)
        {
            var c = new Connection(process, owner);
            _ = Task.Run(c.ReadLoopAsync);
            return c;
        }

        public void Kill(bool intentional)
        {
            _intentional = intentional;
            _process.Kill();
        }

        public ChannelReader<JsonElement> Open(string threadId)
        {
            var channel = Channel.CreateUnbounded<JsonElement>();
            lock (_lock) { if (Dead) channel.Writer.TryComplete(); _threads[threadId] = channel; }
            if (Dead) channel.Writer.TryWrite(Ended);
            return channel.Reader;
        }

        public void Close(string threadId) { lock (_lock) { if (_threads.Remove(threadId, out var c)) c.Writer.TryComplete(); } }

        private static readonly JsonElement Ended = JsonDocument.Parse("""{"method":"__ended"}""").RootElement.Clone();

        public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) { if (Dead) throw new AiProviderException("provider_unavailable", "Das Codex-Programm wurde beendet."); _pending[id] = tcs; }
            try
            {
                await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
                return await tcs.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException) { throw new AiProviderException("timeout", "Das Codex-Programm antwortet nicht."); }
            finally { lock (_lock) _pending.Remove(id); }
        }

        public async Task SendAsync(JsonObject message, CancellationToken ct)
        {
            await _write.WaitAsync(ct);
            try { await _input.WriteLineAsync(message.ToJsonString().AsMemory(), ct); await _input.FlushAsync(ct); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { throw new AiProviderException("provider_unavailable", "Das Codex-Programm wurde beendet."); }
            finally { _write.Release(); }
        }

        /// <summary>Bricht die Runde im Programm ab (nach einem Abbruch des Aufrufers); Fehler dabei sind egal.</summary>
        public async Task InterruptAsync(string threadId, string turnId)
        {
            try { await RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }, TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (Exception e) when (e is AiProviderException or OperationCanceledException) { }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                var lines = new LineReader(new StreamReader(_process.StandardOutput, Encoding.UTF8), 4 << 20);
                while (await lines.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                    using (doc)
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object) continue;
                        var hasId = root.TryGetProperty("id", out var id);
                        var hasMethod = root.TryGetProperty("method", out _);
                        if (hasId && hasMethod)
                        {
                            // Anfragen des Programms (Freigaben, Werkzeuge) beantwortet JulOS nie: sie werden abgelehnt
                            try { await SendAsync(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["error"] = new JsonObject { ["code"] = CodexProtocol.MethodNotFound, ["message"] = "Not supported by JulOS." } }, CancellationToken.None); }
                            catch (AiProviderException) { }
                        }
                        else if (hasId) Resolve(id, root);
                        else if (hasMethod) Route(root);
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or AiProviderException) { Kill(intentional: false); }
            finally { OnEnded(); _process.Dispose(); }
        }

        private void Resolve(JsonElement id, JsonElement root)
        {
            if (!id.TryGetInt64(out var n)) return;
            TaskCompletionSource<JsonElement>? tcs;
            lock (_lock) _pending.TryGetValue(n, out tcs);
            if (tcs is null) return;
            if (root.TryGetProperty("error", out var error))
                tcs.TrySetException(new AiProviderException("provider_error", AiText.Safe(CodexProtocol.Text(error, "message")) is { Length: > 0 } m ? m : "Codex hat die Anfrage abgelehnt."));
            else tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : Empty);
        }

        private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

        private void Route(JsonElement root)
        {
            if (!root.TryGetProperty("params", out var p) || CodexProtocol.Text(p, "threadId") is not { } threadId) return;
            Channel<JsonElement>? channel;
            lock (_lock) _threads.TryGetValue(threadId, out channel);
            channel?.Writer.TryWrite(root.Clone());
        }

        private void OnEnded()
        {
            List<TaskCompletionSource<JsonElement>> pending; List<Channel<JsonElement>> threads;
            lock (_lock) { Dead = true; pending = [.. _pending.Values]; threads = [.. _threads.Values]; }
            // Ein Ende, das niemand wollte, und nur bei einer Verbindung, die schon stand: Wartezeit bis zum naechsten Start. Zuerst buchen, dann die Wartenden wecken,
            // damit schon ihr naechster Versuch die Wartezeit sieht.
            if (!_intentional && ReferenceEquals(_owner._connection, this)) { Interlocked.CompareExchange(ref _owner._connection, null, this); _owner.NoteFailure(); }
            foreach (var t in pending) t.TrySetException(new AiProviderException("provider_unavailable", "Das Codex-Programm wurde beendet."));
            foreach (var c in threads) { c.Writer.TryWrite(Ended); c.Writer.TryComplete(); }
        }
    }
}
