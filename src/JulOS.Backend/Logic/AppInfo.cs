using System.Reflection;

namespace JulOS.Backend.Logic;

/// <summary>
/// Version und Herkunft dieses Servers. Die Version steht an genau einer Stelle (Directory.Build.props); der Commit kommt vom Build
/// (SourceRevisionId, er haengt als "+commit" an der Informationsversion), die Bauzeit aus JULOS_BUILD_DATE.
/// </summary>
public sealed record AppInfo(string Version, string? Commit, string? Built)
{
    public static AppInfo From(Assembly assembly, string? built) =>
        Parse(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, built);

    public static AppInfo Parse(string? informationalVersion, string? built)
    {
        var text = (informationalVersion ?? "").Trim();
        var plus = text.IndexOf('+');
        var version = plus < 0 ? text : text[..plus];
        var commit = plus < 0 ? "" : text[(plus + 1)..];
        return new AppInfo(
            version.Length == 0 ? "0.0.0" : version,
            commit.Length == 0 ? null : commit[..Math.Min(7, commit.Length)],
            string.IsNullOrWhiteSpace(built) ? null : built.Trim());
    }
}
