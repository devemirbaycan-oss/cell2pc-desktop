using Avalonia;
using System;

namespace PocketModem.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // First thing: a crash during startup is exactly the one nobody can
        // otherwise explain.
        CrashLog.Install();
        Run(args);
    }

    private static void Run(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Record before rethrowing: the process still exits, but with a
            // trace on disk rather than vanishing.
            CrashLog.Write("startup", ex);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
#endif
            .WithInterFont()
            .LogToTrace();
}
