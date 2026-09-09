using System.Text.Json;

namespace PocketModem.Cli;

/// <summary>
/// Terminal output: colour when a human is watching, JSON when asked.
///
/// Colour is suppressed when output is redirected or NO_COLOR is set, so piping
/// into a file or another program does not produce escape sequences.
/// </summary>
internal static class Output
{
    private static bool _colour = !Console.IsOutputRedirected;
    private static bool _quiet;
    private static bool _json;

    public static void Configure(bool colour, bool quiet, bool json)
    {
        _colour = colour && !Console.IsOutputRedirected;
        _quiet = quiet;
        _json = json;
    }

    private const string Reset = "[0m";
    private const string Dim = "[2m";
    private const string Bold = "[1m";
    private const string Red = "[31m";
    private const string Green = "[32m";
    private const string Yellow = "[33m";
    private const string Cyan = "[36m";

    private static string Paint(string text, string colour) =>
        _colour ? $"{colour}{text}{Reset}" : text;

    public static void Title(string text)
    {
        if (_quiet || _json) return;
        Console.WriteLine();
        Console.WriteLine(Paint(text, Bold));
        Console.WriteLine(Paint(new string('-', text.Length), Dim));
    }

    public static void Info(string text)
    {
        if (_quiet || _json) return;
        Console.WriteLine(text);
    }

    public static void Step(string text)
    {
        if (_quiet || _json) return;
        Console.WriteLine($"  {text}");
    }

    public static void Ok(string text)
    {
        if (_quiet || _json) return;
        Console.WriteLine($"  {Paint("ok", Green)}  {text}");
    }

    public static void Warn(string text)
    {
        if (_json) return;
        Console.WriteLine($"  {Paint("!", Yellow)}   {text}");
    }

    public static void Fail(string text)
    {
        if (_json) return;
        Console.WriteLine($"  {Paint("x", Red)}   {text}");
    }

    public static void Error(string text)
    {
        if (_json)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = text }));
            return;
        }
        Console.Error.WriteLine($"{Paint("error:", Red)} {text}");
    }

    public static void Hint(string text)
    {
        if (_quiet || _json) return;
        Console.WriteLine($"        {Paint(text, Dim)}");
    }

    public static void Field(string label, string value)
    {
        if (_quiet || _json) return;
        Console.WriteLine($"  {Paint(label.PadRight(14), Dim)} {value}");
    }

    public static void Json(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Rewrites one line in place, for live stats. Falls back to periodic
    /// lines when redirected, so a log file does not fill with control codes.
    /// </summary>
    public static void StatusLine(string text)
    {
        if (_quiet || _json) return;
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
            return;
        }
        int width = SafeWidth();
        string padded = text.Length >= width ? text[..(width - 1)] : text.PadRight(width - 1);
        Console.Write($"\r{padded}");
    }

    public static void EndStatusLine()
    {
        if (_quiet || _json || Console.IsOutputRedirected) return;
        Console.WriteLine();
    }

    private static int SafeWidth()
    {
        try { return Math.Max(Console.WindowWidth, 40); }
        catch { return 80; }   // no console attached (service, redirected)
    }

    public static string Speed(double mbps) =>
        Paint($"{mbps,6:F1} Mbps", mbps > 0.1 ? Cyan : Dim);

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
