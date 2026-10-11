using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

namespace Wunschkino.Auth;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed class Argon2IdPasswordHasher : IPasswordHasher
{
    private const int SaltLength = 16;
    private const int TimeCost = 4;
    private const int MemoryCostKb = 65536;
    private const int Parallelism = 2;

    public string Hash(string password)
    {
        var config = new Argon2Config
        {
            Type = Argon2Type.HybridAddressing,
            Version = Argon2Version.Nineteen,
            TimeCost = TimeCost,
            MemoryCost = MemoryCostKb,
            Lanes = Parallelism,
            Threads = Parallelism,
            Password = Encoding.UTF8.GetBytes(password),
            Salt = RandomNumberGenerator.GetBytes(SaltLength),
            HashLength = 32
        };

        using var argon2 = new Argon2(config);
        using SecureArray<byte> hash = argon2.Hash();
        return config.EncodeString(hash.Buffer);
    }

    public bool Verify(string password, string hash) => Argon2.Verify(hash, password);
}
