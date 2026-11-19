namespace JulOS.Backend.Logic;

/// <summary>Neue Datei, die nur der Server-Benutzer lesen und schreiben darf (0600 unter Linux, schon beim Anlegen, nicht erst danach).</summary>
public static class PrivateFile
{
    public static FileStream Create(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
