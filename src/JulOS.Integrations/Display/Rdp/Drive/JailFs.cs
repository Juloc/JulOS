using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JulOS.Integrations.Display.Rdp.Drive;

/// <summary>
/// Oeffnet Dateien und Ordner unterhalb der Wurzel eines <see cref="DriveJail"/> so, dass kein Symlink und kein ".." nach draussen fuehrt, auch nicht,
/// wenn jemand zwischen Pruefung und Oeffnen etwas austauscht: <c>openat2</c> mit <c>RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS</c>
/// (Linux 5.6 oder neuer) relativ zu einem Deskriptor der Wurzel. Geht das nicht (aeltere Kernel, strenge seccomp-Regeln), oeffnet es mit
/// <c>O_NOFOLLOW</c> ueber den Pfad. In beiden Faellen wird danach geprueft, wohin der Deskriptor wirklich zeigt (<c>/proc/self/fd</c>), und erst dann
/// wird etwas verkuerzt oder geschrieben: ein Oeffnen mit Abschneiden gibt es nicht, <see cref="Truncate"/> kommt nach der Pruefung.
/// </summary>
public sealed partial class JailFs : IDisposable
{
    // Linux, x86-64 und arm64 gleich
    private const int ReadOnly = 0, WriteOnly = 1, ReadWrite = 2, Create = 0x40, Exclusive = 0x80, NonBlocking = 0x800, CloseOnExec = 0x80000;
    private const ulong ResolveNoMagicLinks = 2, ResolveNoSymlinks = 4, ResolveBeneath = 8;
    private const long SysOpenat2 = 437;                // seit Linux 5.6 auf allen Architekturen dieselbe Nummer
    private const int ENOENT = 2, ENOSYS = 38, EPERM = 1;

    // O_NOFOLLOW unterscheidet sich zwischen den Architekturen (nur fuer den Rueckfall)
    private static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;

    private readonly DriveJail _jail;
    private readonly SafeFileHandle? _rootFd;
    private bool _openat2 = true;

    public JailFs(DriveJail jail)
    {
        _jail = jail;
        if (OperatingSystem.IsLinux())
        {
            var fd = Open(jail.Root, ReadOnly | CloseOnExec);
            _rootFd = fd < 0 ? null : new SafeFileHandle(fd, ownsHandle: true);
        }
        else
        {
            throw new PlatformNotSupportedException("Die Laufwerksumleitung gibt es nur auf Linux.");
        }
    }

    public DriveJail Jail => _jail;

    /// <summary>Ob openat2 benutzt wird (false: der Rueckweg mit O_NOFOLLOW und Nachpruefung, auch wenn <see cref="ForceFallback"/> ihn erzwang).</summary>
    public bool UsesOpenat2 => _openat2;

    /// <summary>Nur fuer Tests: den Rueckweg erzwingen, damit auch er geprueft wird.</summary>
    internal void ForceFallback() => _openat2 = false;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial long Syscall(long number, int dirfd, string path, ref OpenHow how, nuint size);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags, int mode = 0);

    /// <summary>Warum ein Oeffnen scheiterte (errno des Kernels, oder 13 = verweigert, wenn die Nachpruefung ablehnte).</summary>
    public enum Failure { None, NotFound, Denied, Other }

    public static Failure Classify(int errno) => errno switch
    {
        0 => Failure.None,
        ENOENT or 20 => Failure.NotFound,                // ENOTDIR: ein Teil des Pfades ist keine Ordner
        1 or 13 or 18 or 40 => Failure.Denied,           // EPERM, EACCES, EXDEV (aus der Wurzel heraus), ELOOP (Symlink)
        _ => Failure.Other,
    };

    /// <summary>
    /// Oeffnet den Eintrag, den die Namen bezeichnen (leer = die Wurzel). Ein Ordner oeffnet sich mit "readOnly" wie eine Datei (zum Auflisten).
    /// <paramref name="create"/>: nur anlegen, wenn es ihn nicht gibt, sonst scheitert es (O_EXCL: ein Symlink an der Stelle wird nie verfolgt).
    /// </summary>
    public SafeFileHandle? Open(string[] segments, bool write, bool create, out int errno)
    {
        errno = 0;
        var flags = (create ? ReadWrite | Create | Exclusive : write ? ReadWrite : ReadOnly) | CloseOnExec | NonBlocking;
        var relative = segments.Length == 0 ? "." : string.Join('/', segments);
        var fd = -1;

        if (_openat2 && _rootFd is { IsInvalid: false } root)
        {
            var how = new OpenHow { Flags = (ulong)flags, Mode = create ? 0x1A4UL : 0, Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagicLinks };   // 0644
            var result = Syscall(SysOpenat2, (int)root.DangerousGetHandle(), relative, ref how, (nuint)Marshal.SizeOf<OpenHow>());
            if (result >= 0)
            {
                fd = (int)result;
            }
            else
            {
                errno = Marshal.GetLastPInvokeError();
                if (errno is ENOSYS or EPERM && !Probe())
                {
                    _openat2 = false;           // der Kernel oder seccomp kennt openat2 nicht: ab jetzt der Rueckweg
                    errno = 0;
                }
                else
                {
                    return null;
                }
            }
        }

        if (fd < 0)
        {
            fd = Open(segments.Length == 0 ? _jail.Root : _jail.Root + "/" + relative, flags | NoFollow, create ? 0x1A4 : 0);
            if (fd < 0)
            {
                errno = Marshal.GetLastPInvokeError();
                return null;
            }
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        var actual = ActualPath(handle);
        if (actual is null || !_jail.Inside(actual))
        {
            // Nur im Rueckweg und nur bei einem Austausch zwischen Pruefung und Oeffnen: eine Datei, die wir eben selbst angelegt haben (O_EXCL) und
            // die ausserhalb gelandet ist, wird dort wieder entfernt (ueber ihren echten Pfad, nicht ueber den Namen).
            if (create && actual is not null)
            {
                try { File.Delete(actual); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* nichts mehr zu tun */ }
            }

            handle.Dispose();
            errno = 13;
            return null;
        }

        return handle;
    }

    // Ob openat2 wirklich fehlt (und nicht nur dieser eine Aufruf verweigert wurde): ein Aufruf, der gelingen muss
    private bool Probe()
    {
        if (_rootFd is not { IsInvalid: false } root)
        {
            return true;
        }

        var how = new OpenHow { Flags = (ulong)(ReadOnly | CloseOnExec), Mode = 0, Resolve = ResolveBeneath };
        var result = Syscall(SysOpenat2, (int)root.DangerousGetHandle(), ".", ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (result >= 0)
        {
            using var _ = new SafeFileHandle((nint)result, ownsHandle: true);
            return true;
        }

        return Marshal.GetLastPInvokeError() is not (ENOSYS or EPERM);
    }

    /// <summary>Wohin der Deskriptor wirklich zeigt (nach allen Symlinks), oder null.</summary>
    public static string? ActualPath(SafeFileHandle handle)
    {
        try
        {
            var target = new FileInfo("/proc/self/fd/" + handle.DangerousGetHandle()).LinkTarget;
            if (target is null)
            {
                return null;
            }

            // Eine inzwischen geloeschte Datei haengt " (deleted)" an: sie war trotzdem in der Wurzel
            return target.EndsWith(" (deleted)", StringComparison.Ordinal) ? target[..^10] : target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Der Pfad, unter dem sich ein geoeffneter Ordner sicher auflisten laesst (der Deskriptor selbst, nicht sein Name).</summary>
    public static string FdPath(SafeFileHandle handle) => "/proc/self/fd/" + handle.DangerousGetHandle();

    /// <summary>Verkuerzt oder verlaengert eine bereits geoeffnete und gepruefte Datei.</summary>
    public static void Truncate(SafeFileHandle handle, long length) => RandomAccess.SetLength(handle, length);

    public void Dispose() => _rootFd?.Dispose();
}
