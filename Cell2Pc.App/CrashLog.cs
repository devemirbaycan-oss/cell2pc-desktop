using System;
using System.IO;
using System.Threading.Tasks;

namespace Cell2Pc.App;

/// <summary>
/// Records why the app died.
///
/// A .NET process is killed outright by an exception escaping a background
/// thread, with nothing printed and no dialog - the app simply vanishes. That
/// leaves nothing to work from, which is the worst position to debug from and
/// exactly how the reported crashes presented.
///
/// This does not prevent a crash; it makes one investigable.
/// </summary>
internal static class CrashLog
{
    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cell2Pc", "crash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write("unhandled", e.ExceptionObject as Exception);
        };

        // A faulted Task whose exception is never observed used to terminate
        // the process and can still be a silent failure; recording it means a
        // background task dying leaves a trace.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved task", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

            var entry =
                $"{Environment.NewLine}" +
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind} ==={Environment.NewLine}" +
                $"{ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}" +
                $"{ex?.StackTrace}{Environment.NewLine}";

            if (ex?.InnerException is not null)
            {
                entry += $"inner: {ex.InnerException.GetType().Name}: " +
                         $"{ex.InnerException.Message}{Environment.NewLine}" +
                         $"{ex.InnerException.StackTrace}{Environment.NewLine}";
            }

            // Append, and keep the file bounded: a crash loop would otherwise
            // fill the disk with the same trace.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 256 * 1024)
                File.Delete(LogPath);

            File.AppendAllText(LogPath, entry);
        }
        catch
        {
            // Failing to record a crash must not itself crash anything.
        }
    }

    /// <summary>The most recent entry, so the app can surface it on next start.</summary>
    public static string? LastCrash()
    {
        try
        {
            if (!File.Exists(LogPath)) return null;
            var text = File.ReadAllText(LogPath);
            int last = text.LastIndexOf("=== ", StringComparison.Ordinal);
            return last < 0 ? null : text[last..].Trim();
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try { if (File.Exists(LogPath)) File.Delete(LogPath); } catch { }
    }

    public static string Location => LogPath;
}
