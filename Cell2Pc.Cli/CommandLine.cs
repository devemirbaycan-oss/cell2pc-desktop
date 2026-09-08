using Cell2Pc.Client.Platform;

namespace Cell2Pc.Cli;

/// <summary>
/// Argument parsing and help text.
///
/// Hand-rolled rather than pulling in a parser library: the surface is six
/// verbs and a handful of flags, and a dependency here would be larger than
/// the code it replaced.
/// </summary>
internal sealed class CommandLine
{
    public string Verb { get; private init; } = "connect";
    /// <summary>True when the user named a verb rather than defaulting.</summary>
    public bool VerbExplicit { get; private init; }
    public string Phone { get; private init; } = PlatformFactory.PhoneAddress;
    public string? Token { get; private init; }
    public string? Passphrase { get; private init; }
    public bool AutoJoin { get; private init; } = true;
    public bool Json { get; private init; }
    public bool Verbose { get; private init; }
    public bool Quiet { get; private init; }
    public bool NoColour { get; private init; }
    public bool ShowHelp { get; private init; }
    public bool ShowVersion { get; private init; }
    public int? DurationSeconds { get; private init; }
    public string? Dns { get; private init; }

    /// <summary>
    /// Positional arguments after the verb, for subcommands like
    /// `split bypass steamcontent.com`.
    /// </summary>
    public IReadOnlyList<string> Rest { get; private init; } = Array.Empty<string>();

    /// <summary>Verbs whose remaining arguments are their own, not a host.</summary>
    private static readonly string[] SubcommandVerbs = { "split" };

    private static readonly string[] Verbs =
        { "connect", "status", "test", "recover", "doctor", "handover",
          "split", "install-service", "uninstall-service", "run-service" };

    public static CommandLine Parse(string[] args)
    {
        string verb = "connect";
        string phone = PlatformFactory.PhoneAddress;
        string? token = Environment.GetEnvironmentVariable("CELL2PC_TOKEN");
        string? passphrase = Environment.GetEnvironmentVariable("CELL2PC_PASSPHRASE");
        bool autoJoin = true, json = false, verbose = false, quiet = false;
        bool noColour = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        bool help = false, version = false;
        int? duration = null;
        string? dns = null;

        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "-h" or "--help": help = true; break;
                case "-V" or "--version": version = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "-q" or "--quiet": quiet = true; break;
                case "--json": json = true; break;
                case "--no-color" or "--no-colour": noColour = true; break;
                case "--no-join": autoJoin = false; break;

                case "-t" or "--token":
                    token = Next(args, ref i, "--token");
                    break;
                case "-p" or "--passphrase":
                    passphrase = Next(args, ref i, "--passphrase");
                    break;
                case "--phone":
                    phone = Next(args, ref i, "--phone");
                    break;
                case "--for":
                    duration = ParseDuration(Next(args, ref i, "--for"));
                    break;
                case "--dns":
                    dns = Next(args, ref i, "--dns");
                    break;

                default:
                    if (a.StartsWith("--token=")) token = a["--token=".Length..];
                    else if (a.StartsWith("--passphrase=")) passphrase = a["--passphrase=".Length..];
                    else if (a.StartsWith("--phone=")) phone = a["--phone=".Length..];
                    else if (a.StartsWith("--for=")) duration = ParseDuration(a["--for=".Length..]);
                    else if (a.StartsWith("--dns=")) dns = a["--dns=".Length..];
                    else if (a.StartsWith('-')) throw new ArgumentException($"Unknown option '{a}'");
                    else positional.Add(a);
                    break;
            }
        }

        bool verbExplicit = false;
        if (positional.Count > 0 && Verbs.Contains(positional[0]))
        {
            verb = positional[0];
            verbExplicit = true;
            positional.RemoveAt(0);
        }

        // Some verbs take their own arguments - `split bypass steamcontent.com`
        // - so their leftovers are handed through rather than being checked as
        // a host address.
        var rest = SubcommandVerbs.Contains(verb)
            ? positional.ToList()
            : new List<string>();
        if (rest.Count > 0) positional.Clear();

        // A bare address is a convenience: `cell2pc 192.168.49.1`.
        // Anything else positional is a typo, and silently treating a mistyped
        // verb as an address would run the wrong command against a nonsense
        // host - so reject it.
        if (positional.Count > 0)
        {
            string candidate = positional[0];
            if (!System.Net.IPAddress.TryParse(candidate, out _))
            {
                throw new ArgumentException(
                    $"'{candidate}' is neither a command nor an IP address. " +
                    $"Commands: {string.Join(", ", Verbs)}");
            }
            phone = candidate;
        }

        return new CommandLine
        {
            Verb = verb,
            VerbExplicit = verbExplicit,
            Phone = phone,
            Token = token,
            Passphrase = passphrase,
            AutoJoin = autoJoin,
            Json = json,
            Verbose = verbose,
            Quiet = quiet,
            NoColour = noColour,
            ShowHelp = help,
            ShowVersion = version,
            DurationSeconds = duration,
            Dns = dns,
            Rest = rest,
        };
    }

    private static string Next(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{name} needs a value");
        return args[++i];
    }

    /// <summary>Accepts 30, 30s, 5m, 2h.</summary>
    private static int ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        char unit = text.Length > 0 ? text[^1] : 's';
        string number = char.IsDigit(unit) ? text : text[..^1];

        if (!int.TryParse(number, out int value))
            throw new ArgumentException($"Could not read a duration from '{text}'");

        return unit switch
        {
            'h' => value * 3600,
            'm' => value * 60,
            _ => value,
        };
    }

    public static void PrintHelp(string? verb = null)
    {
        if (verb is "connect") { PrintConnectHelp(); return; }

        Console.WriteLine($"""
        cell2pc {BuildInfo.Version} - your PC on the phone's mobile data

        USAGE
          cell2pc [command] [options]

        COMMANDS
          connect     Route all traffic through the phone (default)
          status      Show whether a tunnel is running, and its interfaces
          test        Check the tunnel path without changing any routing
          doctor      Diagnose why a connection is not working
          recover     Undo routes left behind by a crash
          handover    Take over a running tunnel without dropping it
          split       Choose what goes through the phone and what does not

        UNATTENDED
          install-service    Connect at boot, before login, like a modem
          uninstall-service  Remove it and restore normal routing

        COMMON OPTIONS
          -t, --token CODE        Pairing code shown on the phone
          -p, --passphrase PASS   Wi-Fi passphrase (first connection only)
              --phone ADDRESS     Phone's address (default {PlatformFactory.PhoneAddress})
              --no-join           Do not join the phone's Wi-Fi automatically
              --for DURATION      Disconnect after 30s / 5m / 2h
              --dns ADDRESS       Resolver to use (default 1.1.1.1)
              --json              Machine-readable output
          -q, --quiet             Only errors
          -v, --verbose           Extra detail, including stack traces
              --no-color          Disable colour
          -h, --help              This help, or help for a command
          -V, --version           Version

        ENVIRONMENT
          CELL2PC_TOKEN       Pairing code, so it need not be typed
          CELL2PC_PASSPHRASE  Wi-Fi passphrase
          NO_COLOR                 Disable colour

        EXAMPLES
          cell2pc                          Connect, prompting for the code
          cell2pc -t k7m2xq4p               Connect with a known code
          cell2pc connect --for 2h          Connect for two hours
          cell2pc doctor                    Work out why it will not connect
          cell2pc recover                   Fix routing after a crash

        Requires {(OperatingSystem.IsWindows() ? "Administrator" : "root")} to create the network interface.
        """);
    }

    private static void PrintConnectHelp()
    {
        Console.WriteLine("""
        cell2pc connect - route all PC traffic through the phone

        USAGE
          cell2pc connect [options]

        WHAT IT DOES
          1. Joins the phone's Wi-Fi Direct group (unless --no-join)
          2. Creates a virtual network interface
          3. Points the default route at it
          4. Reconnects on its own if the link drops
          5. Restores normal routing when you stop it

        OPTIONS
          -t, --token CODE        Pairing code shown on the phone
          -p, --passphrase PASS   Wi-Fi passphrase (first connection only)
              --phone ADDRESS     Phone's address
              --no-join           Assume the Wi-Fi is already joined
              --for DURATION      Disconnect after 30s / 5m / 2h

        Stop with Ctrl+C. Routing is restored on exit, on Ctrl+C, and on the
        next start if the process is killed outright.
        """);
    }
}
