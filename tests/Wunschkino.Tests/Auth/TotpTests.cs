using System.Text;
using Wunschkino.Auth;

namespace Wunschkino.Tests.Auth;

public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // Testvektoren aus RFC 6238, Anhang B (SHA-1, acht Ziffern).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void ComputeCode_MatchesRfc6238TestVectors(long unixSeconds, string expected)
    {
        var step = Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.ComputeCode(RfcSecret, step, digits: 8));
        Assert.Equal(expected[2..], Totp.ComputeCode(RfcSecret, step));
    }

    [Fact]
    public void FindMatchingStep_AcceptsOneStepOfClockDrift_ButNotMore()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.TimeStep(now);

        Assert.Equal(step - 1, Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, step + 1), now, 0));
        Assert.Null(Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, step - 2), now, 0));
        Assert.Null(Totp.FindMatchingStep(RfcSecret, Totp.ComputeCode(RfcSecret, step + 2), now, 0));
    }

    [Fact]
    public void FindMatchingStep_RejectsAlreadyUsedSteps()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.TimeStep(now);
        var code = Totp.ComputeCode(RfcSecret, step);

        Assert.Null(Totp.FindMatchingStep(RfcSecret, code, now, lastUsedStep: step));
    }

    [Theory]
    [InlineData("123 456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12a456", null)]
    [InlineData("ABCDE-FGHJK", null)]
    [InlineData(null, null)]
    public void Normalize_AcceptsOnlySixDigits(string? input, string? expected) =>
        Assert.Equal(expected, Totp.Normalize(input));

    [Fact]
    public void Base32_RoundTripsAndMatchesTheKnownEncoding()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(RfcSecret));
        Assert.Equal(RfcSecret, Totp.FromBase32("gezd gnbv gy3t qojq gezd gnbv gy3t qojq"));
        Assert.Null(Totp.FromBase32("GEZD1"));
    }

    [Fact]
    public void BuildUri_ContainsIssuerAccountAndSecret()
    {
        var uri = Totp.BuildUri("Wunschkino", "anna", RfcSecret);

        Assert.Equal("otpauth://totp/Wunschkino%3Aanna?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Wunschkino&digits=6&period=30", uri);
    }
}
