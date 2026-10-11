using Wunschkino.Auth;

namespace Wunschkino.Tests.Helpers;

/// <summary>Schneller Ersatz für Argon2 in Tests, die nicht das Hashing selbst prüfen.</summary>
public sealed class PlainTextHasher : IPasswordHasher
{
    public string Hash(string password) => $"hashed:{password}";
    public bool Verify(string password, string hash) => hash == Hash(password);
}
