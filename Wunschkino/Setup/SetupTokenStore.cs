using System.Security.Cryptography;
using System.Text;

namespace Wunschkino.Setup;

/// <summary>
/// Einmal-Token, das den Einrichtungsassistenten schützt. Es liegt nur als Datei (0600) vor und wird
/// bewusst nie geloggt — install.sh liest die Datei und zeigt das Token dem Admin an.
/// </summary>
public sealed class SetupTokenStore(string dataDirectory)
{
    public const string FileName = "setup-token";

    public string TokenPath { get; } = Path.Combine(dataDirectory, FileName);

    public void EnsureExists()
    {
        if (File.Exists(TokenPath))
            return;

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var writer = new StreamWriter(TokenPath, Encoding.ASCII, options);
        writer.Write(token);
    }

    public bool IsValid(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(TokenPath))
            return false;

        var expected = Encoding.ASCII.GetBytes(File.ReadAllText(TokenPath).Trim());
        var actual = Encoding.ASCII.GetBytes(candidate.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Delete()
    {
        if (File.Exists(TokenPath))
            File.Delete(TokenPath);
    }
}
