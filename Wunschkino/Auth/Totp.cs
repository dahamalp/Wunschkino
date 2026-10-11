using System.Security.Cryptography;
using System.Text;

namespace Wunschkino.Auth;

/// <summary>
/// Zeitbasierte Einmalcodes nach RFC 6238 (HMAC-SHA1, 6 Ziffern, 30 Sekunden), wie sie alle gängigen Authenticator-Apps erzeugen.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int SecretBytes = 20;
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

    // Ein Zeitfenster davor und danach: Handy- und Serveruhr gehen selten genau gleich.
    private const int AllowedDrift = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long TimeStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / (long)Period.TotalSeconds;

    public static string ComputeCode(byte[] secret, long timeStep, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(new string('0', digits));
    }

    /// <summary>
    /// Liefert das Zeitfenster, zu dem der Code passt, oder null. Fenster bis einschließlich <paramref name="lastUsedStep"/> zählen
    /// nicht: Ein abgefangener Code lässt sich so kein zweites Mal verwenden.
    /// </summary>
    public static long? FindMatchingStep(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var normalized = Normalize(code);
        if (normalized is null)
            return null;

        var current = TimeStep(now);
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (step > lastUsedStep && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeCode(secret, step)), Encoding.ASCII.GetBytes(normalized)))
                return step;
        }
        return null;
    }

    /// <summary>Entfernt Leerzeichen (manche Apps zeigen „123 456“); liefert null, wenn es kein sechsstelliger Code ist.</summary>
    public static string? Normalize(string? code)
    {
        var digits = code?.Replace(" ", "", StringComparison.Ordinal).Trim();
        return digits is { Length: Digits } && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    public static string BuildUri(string issuer, string account, byte[] secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        return $"otpauth://totp/{label}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={(int)Period.TotalSeconds}";
    }

    public static string ToBase32(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    public static byte[]? FromBase32(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant())
        {
            if (c is ' ' or '-' or '=')
                continue;
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
                return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
