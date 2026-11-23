using System.Net;
using System.Text;

namespace JulOS.Frontend.Oidc;

/// <summary>
/// Die wenigen Seiten der Anmelde-Zentrale, die ein Mensch sieht: Anmelden, "kein Zugang", Fehler, Abmelden. Auf dem Server gebaut (die Anfrage der App liegt in der Adresse,
/// kein Zustand im Browser), alles Fremde HTML-kodiert. Der Rahmenschutz kommt von den Kopfzeilen des Servers (X-Frame-Options DENY, frame-ancestors 'none'): keine dieser Seiten
/// laesst sich einbetten. Skripte nur aus der eigenen Adresse (CSP), darum liegt der Code in /oidc-login.js. Deutsch oder Englisch nach Accept-Language.
/// </summary>
internal static class OidcPages
{
    private static bool English(HttpRequest r) => r.Headers.AcceptLanguage.ToString().TrimStart().StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static IResult Html(int status, string title, string body, string? head = null) =>
        Results.Content($"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"color-scheme\" content=\"light dark\">"
            + $"<meta name=\"robots\" content=\"noindex\"><title>{E(title)}</title><link rel=\"stylesheet\" href=\"/oidc.css\">{head}</head><body><main class=\"oidc-card\">{body}</main></body></html>",
            "text/html; charset=utf-8", Encoding.UTF8, status);

    private static string T(HttpRequest r, string de, string en) => English(r) ? en : de;

    /// <summary>Anmelden. next: die Adresse der Anfrage, zu der es nach der Anmeldung zurueck geht (nur Pfad und Suchteil, vom Server gebaut).</summary>
    public static IResult Login(HttpRequest r, string appName, string next, bool fresh)
    {
        var title = T(r, "Anmelden bei JulOS", "Sign in to JulOS");
        var body = $@"<h1>{E(title)}</h1>
<p class=""hint"">{E(T(r, $"für „{appName}“", $"for “{appName}”"))}{(fresh ? " " + E(T(r, "Die letzte Anmeldung ist zu lange her, bitte noch einmal anmelden.", "Your last sign-in is too old, please sign in again.")) : "")}</p>
<form id=""oidcLogin"" data-next=""{E(next)}"" autocomplete=""on"">
<label>{E(T(r, "Benutzer", "User"))}<input id=""u"" name=""user"" autocomplete=""username"" autocapitalize=""none"" spellcheck=""false"" required></label>
<label>{E(T(r, "Passwort", "Password"))}<input id=""p"" name=""password"" type=""password"" autocomplete=""current-password"" required></label>
<label>{E(T(r, "Code aus der Authenticator-App (oder Wiederherstellungscode)", "Code from your authenticator app (or recovery code)"))}<input id=""c"" name=""code"" inputmode=""numeric"" autocomplete=""one-time-code"" required></label>
<p id=""msg"" class=""msg"" role=""alert""></p>
<button type=""submit"" class=""primary"">{E(T(r, "Anmelden", "Sign in"))}</button>
</form>
<script src=""/oidc-login.js""></script>";
        return Html(200, title, body);
    }

    /// <summary>Kein Zugang: die Person ist angemeldet, hat aber in dieser App keine Rolle. Kein Token, keine Weiterleitung.</summary>
    public static IResult NoAccess(HttpRequest r, string appName)
    {
        var title = T(r, "Kein Zugang", "No access");
        return Html(403, title, $"<h1>{E(title)}</h1><p>{E(T(r, $"Dein Konto hat in „{appName}“ keine Rolle. Bitte den Eigentümer von JulOS um Zugang.", $"Your account has no role in “{appName}”. Please ask the owner of JulOS for access."))}</p>");
    }

    /// <summary>Fehler ohne Weiterleitung. Der Text sagt nicht, ob es die App gibt oder welche Angabe falsch war.</summary>
    public static IResult Problem(HttpRequest r, int status, string de, string en)
    {
        var title = T(r, "Anmeldung nicht möglich", "Sign-in not possible");
        return Html(status, title, $"<h1>{E(title)}</h1><p>{E(T(r, de, en))}</p>");
    }

    public static IResult BadRequest(HttpRequest r) =>
        Problem(r, 400, "Diese Anfrage kann nicht ausgeführt werden. Die App ist bei JulOS nicht oder nicht richtig eingerichtet.", "This request cannot be carried out. The app is not set up correctly at JulOS.");

    public static IResult Unavailable(HttpRequest r) =>
        Problem(r, 503, "Die Anmeldung über JulOS ist nicht eingerichtet.", "Signing in through JulOS is not set up.");

    public static IResult TooMany(HttpRequest r) =>
        Problem(r, 429, "Zu viele Anfragen. Bitte kurz warten.", "Too many requests. Please wait a moment.");

    /// <summary>
    /// Das Cookie ist SameSite=Strict und kommt bei einem Aufruf von einer fremden Seite (der App) nicht mit. Diese Seite laedt dieselbe Adresse noch einmal, von JulOS selbst aus
    /// (meta refresh, kein Skript noetig): jetzt gilt der Aufruf als eigener und das Cookie kommt mit. jb=1 verhindert, dass es im Kreis geht.
    /// </summary>
    public static IResult Bounce(HttpRequest r)
    {
        var url = r.Path + r.QueryString.Add("jb", "1");
        var title = T(r, "Einen Moment", "One moment");
        return Html(200, title, $"<h1>{E(title)}</h1><p><a href=\"{E(url)}\">{E(T(r, "Weiter", "Continue"))}</a></p>", $"<meta http-equiv=\"refresh\" content=\"0;url={E(url)}\">");
    }

    public static IResult SignedOut(HttpRequest r)
    {
        var title = T(r, "Abgemeldet", "Signed out");
        return Html(200, title, $"<h1>{E(title)}</h1><p>{E(T(r, "Du bist bei JulOS abgemeldet. Die Sitzungen der verbundenen Apps werden beendet.", "You are signed out of JulOS. The sessions of the connected apps are being ended."))}</p><p><a href=\"/\">{E(T(r, "Zu JulOS", "To JulOS"))}</a></p>");
    }

    /// <summary>Abmelden bestaetigen (kein gueltiger id_token_hint: jede Seite koennte sonst jemanden abmelden). Ein Formular mit POST, vom Server auf denselben Origin geprueft.</summary>
    public static IResult ConfirmLogout(HttpRequest r, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var title = T(r, "Bei JulOS abmelden?", "Sign out of JulOS?");
        var hidden = string.Concat(fields.Select(f => $"<input type=\"hidden\" name=\"{E(f.Key)}\" value=\"{E(f.Value)}\">"));
        return Html(200, title, $"<h1>{E(title)}</h1><p>{E(T(r, "Auch die Sitzungen der verbundenen Apps werden beendet.", "The sessions of the connected apps are ended too."))}</p>"
            + $"<form method=\"post\" action=\"/oidc/end-session\">{hidden}<button type=\"submit\" class=\"primary\">{E(T(r, "Abmelden", "Sign out"))}</button> <a href=\"/\">{E(T(r, "Abbrechen", "Cancel"))}</a></form>");
    }
}
