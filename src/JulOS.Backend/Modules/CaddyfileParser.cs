namespace JulOS.Backend.Modules;

/// <summary>
/// Liest die Adressen und Ziele aus einem Caddyfile (nur lesen, nichts wird ausgefuehrt). Verstanden werden Site-Bloecke mit
/// reverse_proxy, php_fastcgi, file_server (mit root), redir, respond und die Verschachtelung ueber handle, handle_path und route.
/// Nicht verstanden: import, Snippets (werden uebersprungen), Heredocs, Umgebungsvariablen (bleiben als Text stehen). Was nicht erkannt wird,
/// fehlt in der Liste, es wird nie geraten. Alles andere (header_up, tls, basic_auth, ...) wird ueberlesen und kommt deshalb nie in die Anzeige.
/// </summary>
public static class CaddyfileParser
{
    public const int MaxRoutes = 500, MaxDepth = 8;

    private readonly record struct Tok(string Text, bool Quoted);

    private sealed class Node(List<string> tokens, bool block)
    {
        public List<string> Tokens { get; } = tokens;
        public bool IsBlock { get; } = block;
        public List<Node> Children { get; } = [];
    }

    public static CaddyRoute[] Parse(string text)
    {
        var top = BuildTree(Tokenize(text));
        var routes = new List<CaddyRoute>();

        // Ein einziger Site ohne Klammern: erste Zeile = Adressen, der Rest = Inhalt
        var sites = top.Where(n => n.Tokens.Count > 0 && !n.Tokens[0].StartsWith('(') && n.Tokens[0] != "import").ToList();
        if (sites.Count > 0 && sites.All(n => !n.IsBlock) && !top.Any(n => n.IsBlock && n.Tokens.Count > 0 && !n.Tokens[0].StartsWith('(')))
        {
            var one = new Node(sites[0].Tokens, true);
            one.Children.AddRange(sites.Skip(1));
            sites = [one];
        }

        foreach (var site in sites)
        {
            if (!site.IsBlock || routes.Count >= MaxRoutes) continue;
            var domains = Addresses(site.Tokens);
            if (domains.Length == 0) continue;
            var before = routes.Count;
            Walk(site.Children, domains, null, routes, 0);
            if (routes.Count == before) foreach (var d in domains) routes.Add(new CaddyRoute(d, null, "other", []));
        }
        return [.. routes.Take(MaxRoutes)];
    }

    private static string[] Addresses(List<string> tokens)
    {
        var list = new List<string>();
        foreach (var t in tokens)
            foreach (var part in t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var a = part;
                foreach (var prefix in new[] { "https://", "http://" }) if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) a = a[prefix.Length..];
                if (a.Length > 0 && !a.StartsWith('@')) list.Add(CaddyRedact.Clip(a));
            }
        return [.. list];
    }

    private static bool IsMatcher(string t) => t.StartsWith('@') || t.StartsWith('/') || t == "*";

    private static void Walk(List<Node> body, string[] domains, string? path, List<CaddyRoute> routes, int depth)
    {
        if (depth > MaxDepth) return;
        var root = body.LastOrDefault(n => n.Tokens.Count > 1 && n.Tokens[0] == "root")?.Tokens[^1];
        foreach (var n in body)
        {
            if (routes.Count >= MaxRoutes) return;
            if (n.Tokens.Count == 0) continue;
            var name = n.Tokens[0];
            var args = n.Tokens.Skip(1).ToList();
            switch (name)
            {
                case "reverse_proxy" or "php_fastcgi":
                {
                    var p = path;
                    if (args.Count > 0 && IsMatcher(args[0])) { if (args[0].StartsWith('/')) p = args[0]; args.RemoveAt(0); }
                    foreach (var child in n.Children.Where(c => c.Tokens.Count > 1 && c.Tokens[0] == "to")) args.AddRange(child.Tokens.Skip(1));
                    var targets = args.Select(CaddyRedact.Target).Distinct().ToArray();
                    Emit(routes, domains, p, "proxy", targets, name == "php_fastcgi" ? "PHP-FastCGI" : null);
                    break;
                }
                case "file_server":
                    Emit(routes, domains, path, "files", root is null ? [] : [CaddyRedact.Clip(root)]);
                    break;
                case "redir":
                {
                    var p = path;
                    if (args.Count > 1 && IsMatcher(args[0]) && !args[0].Contains("://")) { if (args[0].StartsWith('/')) p = args[0]; args.RemoveAt(0); }
                    var target = args.FirstOrDefault(a => !int.TryParse(a, out _) && a is not ("permanent" or "temporary" or "html"));
                    Emit(routes, domains, p, "redirect", target is null ? [] : [CaddyRedact.Target(target)]);
                    break;
                }
                case "respond":
                {
                    var code = args.LastOrDefault(a => a.Length == 3 && int.TryParse(a, out _));
                    Emit(routes, domains, path, "respond", [], code);
                    break;
                }
                case "handle" or "handle_path" or "route":
                {
                    var p = args.Count > 0 && args[0].StartsWith('/') ? args[0] : path;
                    Walk(n.Children, domains, p, routes, depth + 1);
                    break;
                }
            }
        }
    }

    private static void Emit(List<CaddyRoute> routes, string[] domains, string? path, string kind, string[] targets, string? detail = null)
    {
        foreach (var d in domains)
            if (routes.Count < MaxRoutes) routes.Add(new CaddyRoute(d, path is null ? null : CaddyRedact.Clip(path), kind, targets, detail));
    }

    /* ---------- Zerlegen ---------- */

    private static List<List<Tok>> Tokenize(string text)
    {
        var lines = new List<List<Tok>>();
        var line = new List<Tok>();
        int i = 0, n = text.Length;
        void EndLine() { if (line.Count > 0) { lines.Add(line); line = []; } }
        while (i < n)
        {
            var c = text[i];
            if (c == '\n') { EndLine(); i++; }
            else if (char.IsWhiteSpace(c)) i++;
            else if (c == '\\' && i + 1 < n && (text[i + 1] == '\n' || text[i + 1] == '\r' && i + 2 < n && text[i + 2] == '\n')) i += text[i + 1] == '\n' ? 2 : 3;
            else if (c == '#') { while (i < n && text[i] != '\n') i++; }
            else if (c == '"' || c == '`')
            {
                var q = c; i++;
                var sb = new System.Text.StringBuilder();
                while (i < n && text[i] != q)
                {
                    if (q == '"' && text[i] == '\\' && i + 1 < n) i++;
                    sb.Append(text[i++]);
                }
                i++;
                line.Add(new Tok(sb.ToString(), true));
            }
            else
            {
                var start = i;
                while (i < n && !char.IsWhiteSpace(text[i])) i++;
                line.Add(new Tok(text[start..i], false));
            }
        }
        EndLine();
        return lines;
    }

    private static List<Node> BuildTree(List<List<Tok>> lines)
    {
        var top = new List<Node>();
        var stack = new Stack<List<Node>>();
        var current = top;
        foreach (var line in lines)
        {
            if (line.Count == 1 && line[0] is { Quoted: false, Text: "}" })
            {
                if (stack.Count > 0) current = stack.Pop();
                continue;
            }
            var opens = line[^1] is { Quoted: false, Text: "{" };
            var node = new Node([.. (opens ? line[..^1] : line).Select(t => t.Text)], opens);
            current.Add(node);
            if (opens) { stack.Push(current); current = node.Children; }
        }
        return top;
    }
}
