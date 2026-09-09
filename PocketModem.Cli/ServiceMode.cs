using System.Diagnostics;
using PocketModem.Client;
using PocketModem.Client.Net;
using PocketModem.Client.Platform;

namespace PocketModem.Cli;

/// <summary>
/// Running unattended, the way a modem does.
///
/// A modem is on before you sit down and stays on after you log out. PocketModem
/// has needed someone to open an app and press a button, which is the largest
/// remaining difference in how the two feel to live with.
///
/// As a Windows service this connects at boot, before any user logs in, and
/// keeps trying while the phone is away. Installation is a separate step
/// because it is a real change to the machine and should be asked for
/// explicitly rather than happening on first run.
/// </summary>
internal static class ServiceMode
{
    private const string ServiceName = "PocketModem";

    // ------------------------------------------------------------- install --

    public static int Install(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        if (!OperatingSystem.IsWindows())
            return InstallSystemd(cmd);

        if (!PlatformFactory.IsPrivileged())
        {
            Output.Error("Installing a service needs Administrator rights.");
            return 1;
        }

        string? token = ResolveStoredToken(cmd);
        if (token is null) return 1;

        Output.Title("Install service");

        string exe = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "pocketmodem.exe");

        // The token is passed on the command line rather than stored by the
        // service, so removing the service removes the credential with it.
        string args = $"\"{exe}\" run-service --token={token} --phone={cmd.Phone}";
        if (!string.IsNullOrWhiteSpace(cmd.Passphrase)) args += $" --passphrase={cmd.Passphrase}";

        var (ok, output) = Run("sc.exe",
            $"create {ServiceName} binPath= \"{args}\" start= auto " +
            $"DisplayName= \"PocketModem\"");

        if (!ok && !output.Contains("EXISTS"))
        {
            Output.Fail($"could not create the service: {output.Trim()}");
            return 1;
        }

        Run("sc.exe", $"description {ServiceName} \"Shares a phone's mobile data with this PC.\"");

        // Restart on failure rather than giving up: an unattended connection
        // that stops at the first error is worse than none, because nobody is
        // watching to notice.
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/15000/restart/60000");

        var (started, startOutput) = Run("sc.exe", $"start {ServiceName}");
        if (started) Output.Ok("installed and started");
        else
        {
            Output.Warn($"installed, but did not start: {startOutput.Trim()}");
            Output.Hint("It will start at the next boot.");
        }

        Output.Info("");
        Output.Hint("Remove it later with:  pocketmodem uninstall-service");
        return 0;
    }

    public static int Uninstall(CommandLine cmd)
    {
        Output.Configure(!cmd.NoColour, cmd.Quiet, cmd.Json);

        if (!OperatingSystem.IsWindows())
            return UninstallSystemd();

        if (!PlatformFactory.IsPrivileged())
        {
            Output.Error("Removing a service needs Administrator rights.");
            return 1;
        }

        Output.Title("Remove service");
        Run("sc.exe", $"stop {ServiceName}");
        Thread.Sleep(1500);

        var (ok, output) = Run("sc.exe", $"delete {ServiceName}");
        if (!ok && !output.Contains("does not exist"))
        {
            Output.Fail($"could not remove it: {output.Trim()}");
            return 1;
        }

        // Leaving routes behind on an uninstall is the worst outcome available:
        // no service, no app, and no internet.
        PlatformFactory.CreateRouteManager().RecoverIfNeeded();

        Output.Ok("removed, and routing restored");
        return 0;
    }

    // ----------------------------------------------------------------- run --

    /// <summary>
    /// The service body. Keeps trying rather than exiting, because nobody is
    /// watching to restart it.
    /// </summary>
    public static async Task<int> Run(CommandLine cmd)
    {
        Output.Configure(colour: false, quiet: true, json: false);

        string? token = cmd.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            EventLog("No pairing code supplied; nothing to do.");
            return 1;
        }

        var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

        int backoff = 5;

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (!PlatformFactory.IsJoinedToPhone() && !string.IsNullOrWhiteSpace(cmd.Passphrase))
                {
                    var joiner = PlatformFactory.CreateWifiJoiner();
                    joiner.Leave();
                    await Task.Delay(500, stopping.Token);
                    await joiner.JoinAsync(cmd.Passphrase!, stopping.Token);
                }

                if (PlatformFactory.IsJoinedToPhone())
                {
                    using var session = new TunnelSession(cmd.Phone, token, cmd.Passphrase)
                    {
                        Split = SplitRules.Load(),
                    };

                    if (await session.StartAsync(stopping.Token))
                    {
                        EventLog("Connected.");
                        backoff = 5;

                        // Hold while it works; the session reconnects on its own.
                        while (!stopping.IsCancellationRequested && session.IsRunning)
                            await Task.Delay(5000, stopping.Token);

                        session.Stop();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                EventLog($"Attempt failed: {ex.Message}");
            }

            // Back off so a phone that is simply switched off does not mean a
            // reconnect attempt every second all night.
            try { await Task.Delay(TimeSpan.FromSeconds(backoff), stopping.Token); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, 120);
        }

        return 0;
    }

    // --------------------------------------------------------------- linux --

    private static int InstallSystemd(CommandLine cmd)
    {
        if (!PlatformFactory.IsPrivileged())
        {
            Output.Error("Installing a service needs root.");
            return 1;
        }

        string? token = ResolveStoredToken(cmd);
        if (token is null) return 1;

        string exe = Environment.ProcessPath ?? "/usr/local/bin/pocketmodem";
        string unit = $"""
        [Unit]
        Description=PocketModem - shares a phone's mobile data with this PC
        After=network.target

        [Service]
        Type=simple
        ExecStart={exe} run-service --token={token} --phone={cmd.Phone}{(string.IsNullOrWhiteSpace(cmd.Passphrase) ? "" : $" --passphrase={cmd.Passphrase}")}
        Restart=always
        RestartSec=5

        [Install]
        WantedBy=multi-user.target
        """;

        try
        {
            // 0600: the unit carries the pairing code, so it should not be
            // world-readable.
            const string path = "/etc/systemd/system/pocketmodem.service";
            File.WriteAllText(path, unit);

            // Guarded rather than merely unreachable: this method throws on
            // Windows, and the analyser is right that nothing in the type
            // system stops it being called there.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Run("systemctl", "daemon-reload");
            Run("systemctl", "enable pocketmodem");
            Run("systemctl", "start pocketmodem");

            Output.Ok("installed and started");
            Output.Hint("Check it with:  systemctl status pocketmodem");
            return 0;
        }
        catch (Exception ex)
        {
            Output.Fail($"could not install: {ex.Message}");
            return 1;
        }
    }

    private static int UninstallSystemd()
    {
        Run("systemctl", "stop pocketmodem");
        Run("systemctl", "disable pocketmodem");
        try { File.Delete("/etc/systemd/system/pocketmodem.service"); } catch { }
        Run("systemctl", "daemon-reload");

        PlatformFactory.CreateRouteManager().RecoverIfNeeded();
        Output.Ok("removed, and routing restored");
        return 0;
    }

    // --------------------------------------------------------------- utils --

    private static string? ResolveStoredToken(CommandLine cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.Token)) return cmd.Token;

        Output.Error("A pairing code is required to install the service.");
        Output.Hint("Pass --token=<code>, so the service can connect unattended.");
        return null;
    }

    private static void EventLog(string message)
    {
        // Plain stdout: the service manager captures it on both platforms, and
        // a log file of our own would be a second place to look.
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}");
    }

    private static (bool Ok, string Output) Run(string file, string args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return (false, "could not start");
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(20000);
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
