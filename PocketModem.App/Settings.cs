using System;
using System.IO;
using System.Text.Json;

namespace PocketModem.App;

/// <summary>
/// Remembers the pairing code so reconnecting is one click.
///
/// Stored as plain JSON under the user's profile. The token authorises a device
/// the user owns to use their own phone's connection; it is not a password to
/// anything else, and a keychain dependency would not change what an attacker
/// with access to this profile could already do.
/// </summary>
internal static class Settings
{
    /// <summary>
    /// What the tunnel uses when the user has expressed no preference.
    ///
    /// The phone's own resolver is not reachable from here - it lives behind
    /// the cellular interface - so a public resolver is the default rather
    /// than a choice. Cloudflare's is picked for latency, and the whole point
    /// of surfacing it is that a person who disagrees can change it.
    /// </summary>
    public const string DefaultDns = "1.1.1.1";

    private static string Path0 => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PocketModem", "settings.json");

    // Dns arrived after the first release, so it is nullable here: a settings
    // file written by an older build has no such field, and deserialising it
    // must yield the default rather than an empty string that would then be
    // handed to the adapter as a resolver address.
    private sealed record Saved(string Token, string Passphrase, string? Dns);

    public static (string Token, string Passphrase, string Dns) Load()
    {
        try
        {
            if (!File.Exists(Path0)) return ("", "", DefaultDns);

            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(Path0));
            string dns = saved?.Dns ?? "";

            return (saved?.Token ?? "",
                    saved?.Passphrase ?? "",
                    string.IsNullOrWhiteSpace(dns) ? DefaultDns : dns.Trim());
        }
        catch
        {
            return ("", "", DefaultDns);
        }
    }

    public static void Save(string token, string passphrase, string dns)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(
                new Saved(token, passphrase, string.IsNullOrWhiteSpace(dns) ? DefaultDns : dns.Trim())));
        }
        catch
        {
            // Losing a remembered code is a small inconvenience, not worth
            // interrupting a working connection over.
        }
    }
}
