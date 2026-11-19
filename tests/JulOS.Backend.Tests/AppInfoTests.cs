using System.Xml.Linq;
using JulOS.Backend.Logic;

namespace JulOS.Backend.Tests;

/// <summary>Version: eine Quelle (Directory.Build.props), der Commit haengt am Build an.</summary>
public sealed class AppInfoTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0", null)]
    [InlineData("0.1.0+abcdef0123456789", "0.1.0", "abcdef0")]
    [InlineData("0.2.0-rc.1+abc", "0.2.0-rc.1", "abc")]
    [InlineData("0.1.0+", "0.1.0", null)]
    [InlineData("", "0.0.0", null)]
    [InlineData(null, "0.0.0", null)]
    public void Version_and_short_commit_come_from_the_informational_version(string? informational, string version, string? commit)
    {
        var info = AppInfo.Parse(informational, " 2026-11-19T01:00:00Z ");
        Assert.Equal(version, info.Version);
        Assert.Equal(commit, info.Commit);
        Assert.Equal("2026-11-19T01:00:00Z", info.Built);
        Assert.Null(AppInfo.Parse(informational, " ").Built);
    }

    [Fact]
    public void The_server_reports_the_version_from_Directory_Build_props()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var declared = XDocument.Load(Path.Combine(dir.FullName, "Directory.Build.props")).Descendants("Version").Single().Value;
        Assert.Equal(declared, AppInfo.From(typeof(Program).Assembly, null).Version);
    }
}

/// <summary>Auswertung der Gesundheitspruefung fuer den Container-Healthcheck.</summary>
public sealed class HealthCheckTests
{
    [Theory]
    [InlineData("{\"ok\":true,\"db\":true,\"version\":\"1\"}", true)]
    [InlineData("{\"ok\":false,\"db\":false,\"version\":\"1\"}", false)]
    [InlineData("{\"db\":true}", false)]
    [InlineData("[true]", false)]
    [InlineData("<html>502</html>", false)]
    [InlineData("", false)]
    public void Only_a_true_ok_counts_as_healthy(string body, bool healthy) => Assert.Equal(healthy, HealthCheck.IsHealthy(body));

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("9000", 9000)]
    [InlineData("9000;9001", 9000)]
    [InlineData("0", 8080)]
    [InlineData("70000", 8080)]
    [InlineData("abc", 8080)]
    public void The_port_comes_from_the_server_setting(string? setting, int port) => Assert.Equal(port, HealthCheck.Port(setting));
}
