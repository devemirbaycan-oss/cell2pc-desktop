using System;
using System.IO;
using System.Text.Json;

namespace Cell2Pc.App;

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
    private static string Path0 => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cell2Pc", "settings.json");

    private sealed record Saved(string Token, string Passphrase);

    public static (string Token, string Passphrase) Load()
    {
        try
        {
            if (!File.Exists(Path0)) return ("", "");
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(Path0));
            return (saved?.Token ?? "", saved?.Passphrase ?? "");
        }
        catch
        {
            return ("", "");
        }
    }

    public static void Save(string token, string passphrase)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(new Saved(token, passphrase)));
        }
        catch
        {
            // Losing a remembered code is a small inconvenience, not worth
            // interrupting a working connection over.
        }
    }
}
