using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Services;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class TwoFactorServiceTests : IDisposable
{
    private const string Password = "ein-sicheres-passwort";
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Server = IPAddress.Parse("192.168.1.5");

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly AuditLog _audit;
    private readonly TwoFactorService _service;
    private readonly IpBlockService _blocks;
    private readonly LoginGuard _guard;
    private readonly User _anna;
    private readonly EphemeralDataProtectionProvider _dataProtection = new();
    private readonly SettingsService _settings;

    // Ein neuer Dienst auf derselben Datenbank und demselben Schlüsselring entspricht einem Neustart der App.
    private TwoFactorService NewService() => new(_db, _dataProtection, new PlainTextHasher(), _settings, _time, _audit);

    public TwoFactorServiceTests()
    {
        _audit = new AuditLog(_db, _time, NullLogger<AuditLog>.Instance);
        _settings = new SettingsService(_db, _dataProtection, NullLogger<SettingsService>.Instance);
        _service = NewService();
        _blocks = new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance);
        _guard = new LoginGuard(new LoginService(_db, new PlainTextHasher(), _time, _service), _blocks, _time, NullLogger<LoginGuard>.Instance, _audit);
        _anna = TestUsers.Add(_db, "anna");
    }

    public void Dispose() => _db.Dispose();

    private string CurrentCode(byte[] secret) => Totp.ComputeCode(secret, Totp.TimeStep(_time.GetUtcNow()));

    private async Task<(byte[] Secret, IReadOnlyList<string> RecoveryCodes)> EnableForAnnaAsync()
    {
        var setup = _service.CreateSetup("anna");
        var result = await _service.EnableAsync(_anna.Id, Password, setup.Secret, CurrentCode(setup.Secret));
        Assert.True(result.Success, result.Error);
        // Der Einschalt-Code ist verbraucht; das nächste Zeitfenster liefert einen neuen.
        _time.Advance(Totp.Period);
        return (setup.Secret, result.RecoveryCodes!);
    }

    private async Task<List<string>> AuditActionsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.AuditEntries.OrderBy(e => e.Id).Select(e => e.Action).ToListAsync();
    }

    [Fact]
    public void CreateSetup_ProducesAQrCodeAndAReadableKey()
    {
        var setup = _service.CreateSetup("anna");

        Assert.Equal(Totp.SecretBytes, setup.Secret.Length);
        Assert.StartsWith("data:image/png;base64,", setup.QrCodeDataUri);
        Assert.Equal(Totp.ToBase32(setup.Secret), setup.SecretText.Replace(" ", ""));
    }

    [Fact]
    public async Task Enable_WithWrongCode_StaysOff()
    {
        var setup = _service.CreateSetup("anna");

        var result = await _service.EnableAsync(_anna.Id, Password, setup.Secret, "000000");

        Assert.False(result.Success);
        Assert.False((await _service.GetStatusAsync(_anna.Id)).Enabled);
    }

    [Fact]
    public async Task Enable_NeedsThePassword()
    {
        var setup = _service.CreateSetup("anna");

        var result = await _service.EnableAsync(_anna.Id, "falsch-falsch-falsch", setup.Secret, CurrentCode(setup.Secret));

        Assert.False(result.Success);
        Assert.False((await _service.GetStatusAsync(_anna.Id)).Enabled);
    }

    [Fact]
    public async Task WrongCodesOnTheAccountPage_LockOnlyThatPage_NeverTheSignIn()
    {
        var (secret, _) = await EnableForAnnaAsync();
        for (var i = 0; i < TwoFactorService.MaxManagementFailures; i++)
            await _service.DisableAsync(_anna.Id, Password, "000000");

        var locked = await _service.DisableAsync(_anna.Id, Password, CurrentCode(secret));
        var signIn = await _guard.AttemptAsync("anna", Password, Client, Server);

        Assert.False(locked.Success);
        Assert.True(signIn.RequiresSecondFactor);
        Assert.Null(TestUsers.Reload(_db, _anna.Id).LockedUntilUtc);
    }

    [Fact]
    public async Task RecoveryCodes_AreStoredAsKeyedHashes_NotAsThePlainCode()
    {
        var (_, codes) = await EnableForAnnaAsync();

        await using var check = _db.CreateDbContext();
        var hashes = await check.RecoveryCodes.Select(c => c.CodeHash).ToListAsync();
        Assert.Equal(TwoFactorService.RecoveryCodeCount, hashes.Distinct().Count());
        Assert.All(hashes, hash => Assert.StartsWith("h1:", hash));
        Assert.All(hashes, hash => Assert.DoesNotContain(codes[0].Replace("-", ""), hash));
        Assert.Equal(SecondFactorKind.RecoveryCode, await _service.VerifyAsync(_anna.Id, codes[0]));
    }

    [Fact]
    public async Task TheRecoveryKey_IsKeptEncrypted_AndASecondServiceInstanceAcceptsTheSameCodes()
    {
        var (_, codes) = await EnableForAnnaAsync();

        await using (var check = _db.CreateDbContext())
        {
            var stored = await check.Settings.SingleAsync(s => s.Key == SettingKeys.RecoveryCodeKey);
            Assert.True(stored.IsProtected);
        }

        Assert.Equal(SecondFactorKind.RecoveryCode, await NewService().VerifyAsync(_anna.Id, codes[3]));
    }

    [Fact]
    public async Task OlderArgon2StyleRecoveryCodes_StillWorkOnce()
    {
        await EnableForAnnaAsync();
        await using (var db = _db.CreateDbContext())
        {
            db.RecoveryCodes.Add(new RecoveryCode { UserId = _anna.Id, CodeHash = new PlainTextHasher().Hash("ABCDE12345") });
            await db.SaveChangesAsync();
        }

        Assert.Equal(SecondFactorKind.RecoveryCode, await _service.VerifyAsync(_anna.Id, "abcde-12345"));
        Assert.Equal(SecondFactorKind.None, await _service.VerifyAsync(_anna.Id, "abcde-12345"));
    }

    [Fact]
    public async Task SecondStep_AfterTenWrongCodes_IsLockedForTheAccount_EvenFromOtherAddresses()
    {
        var (secret, _) = await EnableForAnnaAsync();
        var pending = (await _guard.AttemptAsync("anna", Password, Client, Server)).Pending!;
        for (var i = 0; i < SecondFactorThrottle.FailuresPerStage; i++)
            await WithClockRunning(_guard.AttemptSecondFactorAsync(pending, "000000", IPAddress.Parse($"198.51.100.{i + 1}"), Server));

        var result = await WithClockRunning(_guard.AttemptSecondFactorAsync(pending, CurrentCode(secret), IPAddress.Parse("198.51.100.200"), Server));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task TheSecondStepLock_SurvivesARestart_ExpiresAndGrowsWithRepeatedFailures()
    {
        var (secret, _) = await EnableForAnnaAsync();
        var pending = (await _guard.AttemptAsync("anna", Password, Client, Server)).Pending!;
        for (var i = 0; i < SecondFactorThrottle.FailuresPerStage; i++)
            await WithClockRunning(_guard.AttemptSecondFactorAsync(pending, "000000", IPAddress.Parse($"198.51.100.{i + 1}"), Server));

        var restarted = new LoginGuard(new LoginService(_db, new PlainTextHasher(), _time, NewService()),
            new IpBlockService(_db, _time, TrustedProxies.None, NullLogger<IpBlockService>.Instance), _time, NullLogger<LoginGuard>.Instance);
        var whileLocked = await WithClockRunning(restarted.AttemptSecondFactorAsync(pending, CurrentCode(secret), IPAddress.Parse("198.51.100.201"), Server));
        Assert.False(whileLocked.Success);
        Assert.Contains("zu viele", whileLocked.Error!, StringComparison.OrdinalIgnoreCase);

        _time.Advance(SecondFactorThrottle.Stages[0] + TimeSpan.FromMinutes(1));
        await WithClockRunning(restarted.AttemptSecondFactorAsync(pending, "000000", IPAddress.Parse("198.51.100.202"), Server));
        Assert.False(SecondFactorThrottle.IsLocked(TestUsers.Reload(_db, _anna.Id), _time.GetUtcNow().UtcDateTime));

        for (var i = 0; i < SecondFactorThrottle.FailuresPerStage - 1; i++)
            await WithClockRunning(restarted.AttemptSecondFactorAsync(pending, "000000", IPAddress.Parse($"198.51.101.{i + 1}"), Server));
        var lockedAgain = TestUsers.Reload(_db, _anna.Id);
        Assert.True(SecondFactorThrottle.IsLocked(lockedAgain, _time.GetUtcNow().UtcDateTime));
        Assert.True(lockedAgain.SecondFactorLockedUntilUtc > _time.GetUtcNow().UtcDateTime + SecondFactorThrottle.Stages[0]);

        _time.Advance(SecondFactorThrottle.Stages[1] + TimeSpan.FromMinutes(1));
        var success = await WithClockRunning(restarted.AttemptSecondFactorAsync(pending, CurrentCode(secret), IPAddress.Parse("198.51.100.203"), Server));
        Assert.True(success.Success);
        Assert.Equal(0, TestUsers.Reload(_db, _anna.Id).SecondFactorFailures);
    }

    /// <summary>Ab fünf Fehlversuchen bremst der Guard per Task.Delay auf der Fake-Uhr; ohne Weiterstellen würde der Test ewig warten.</summary>
    private async Task<LoginResult> WithClockRunning(Task<LoginResult> attempt)
    {
        while (!attempt.IsCompleted)
        {
            _time.Advance(LoginGuard.Slowdown);
            await Task.Yield();
        }

        return await attempt;
    }

    [Fact]
    public async Task SecondStep_WithARecoveryCode_SaysSo()
    {
        var (_, codes) = await EnableForAnnaAsync();
        var pending = (await _guard.AttemptAsync("anna", Password, Client, Server)).Pending!;

        var result = await _guard.AttemptSecondFactorAsync(pending, codes[3], Client, Server);

        Assert.True(result.Success);
        Assert.True(result.UsedRecoveryCode);
    }

    [Fact]
    public async Task Enable_StoresTheSecretEncrypted_AndHandsOutTenRecoveryCodesOnce()
    {
        var (secret, codes) = await EnableForAnnaAsync();

        Assert.Equal(TwoFactorService.RecoveryCodeCount, codes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        var stored = TestUsers.Reload(_db, _anna.Id);
        Assert.NotNull(stored.TotpSecret);
        Assert.DoesNotContain(Totp.ToBase32(secret), stored.TotpSecret);
        await using var db = _db.CreateDbContext();
        Assert.DoesNotContain(await db.RecoveryCodes.Select(c => c.CodeHash).ToListAsync(), hash => codes.Contains(hash));
        Assert.Equal(new TwoFactorStatus(true, 10), await _service.GetStatusAsync(_anna.Id));
        Assert.Contains(AuditActions.TwoFactorEnabled, await AuditActionsAsync());
    }

    [Fact]
    public async Task Verify_AcceptsEachCodeOnlyOnce()
    {
        var (secret, _) = await EnableForAnnaAsync();
        var code = CurrentCode(secret);

        Assert.Equal(SecondFactorKind.Totp, await _service.VerifyAsync(_anna.Id, code));
        Assert.Equal(SecondFactorKind.None, await _service.VerifyAsync(_anna.Id, code));
    }

    [Fact]
    public async Task Verify_RecoveryCodeWorksOnce_InAnySpelling()
    {
        var (_, codes) = await EnableForAnnaAsync();

        Assert.Equal(SecondFactorKind.RecoveryCode, await _service.VerifyAsync(_anna.Id, codes[0].ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(SecondFactorKind.None, await _service.VerifyAsync(_anna.Id, codes[0]));
        Assert.Equal(9, (await _service.GetStatusAsync(_anna.Id)).RecoveryCodesLeft);
    }

    [Fact]
    public async Task Disable_NeedsPasswordAndCode()
    {
        var (secret, _) = await EnableForAnnaAsync();

        Assert.False((await _service.DisableAsync(_anna.Id, "falsch-falsch-falsch", CurrentCode(secret))).Success);
        Assert.False((await _service.DisableAsync(_anna.Id, Password, "000000")).Success);
        Assert.True((await _service.DisableAsync(_anna.Id, Password, CurrentCode(secret))).Success);

        var stored = TestUsers.Reload(_db, _anna.Id);
        Assert.Null(stored.TotpSecret);
        Assert.False(stored.HasTwoFactor);
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.RecoveryCodes.CountAsync());
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_NeedsAnAppCode_AndReplacesTheOldOnes()
    {
        var (secret, oldCodes) = await EnableForAnnaAsync();

        Assert.False((await _service.RegenerateRecoveryCodesAsync(_anna.Id, oldCodes[0])).Success);
        var result = await _service.RegenerateRecoveryCodesAsync(_anna.Id, CurrentCode(secret));

        Assert.True(result.Success);
        Assert.Empty(result.RecoveryCodes!.Intersect(oldCodes));
        Assert.Equal(SecondFactorKind.None, await _service.VerifyAsync(_anna.Id, oldCodes[1]));
    }

    [Fact]
    public async Task ResetByAdmin_TurnsItOff_AndSignsTheAccountOutEverywhere()
    {
        await EnableForAnnaAsync();
        var admin = TestUsers.Add(_db, "chef", UserRole.Admin);
        var stampBefore = TestUsers.Reload(_db, _anna.Id).SecurityStamp;

        Assert.False((await _service.ResetByAdminAsync(_anna.Id, _anna.Id)).Success);
        Assert.True((await _service.ResetByAdminAsync(admin.Id, _anna.Id)).Success);

        var stored = TestUsers.Reload(_db, _anna.Id);
        Assert.False(stored.HasTwoFactor);
        Assert.NotEqual(stampBefore, stored.SecurityStamp);
        Assert.Contains(AuditActions.TwoFactorReset, await AuditActionsAsync());
    }

    [Fact]
    public async Task ResetByAdmin_IsRefusedForNonAdmins()
    {
        await EnableForAnnaAsync();
        var bob = TestUsers.Add(_db, "bob");

        Assert.False((await _service.ResetByAdminAsync(bob.Id, _anna.Id)).Success);
        Assert.True(TestUsers.Reload(_db, _anna.Id).HasTwoFactor);
    }

    [Fact]
    public async Task Login_WithTwoFactor_AsksForTheCodeInsteadOfSigningIn()
    {
        await EnableForAnnaAsync();

        var result = await _guard.AttemptAsync("anna", Password, Client, Server);

        Assert.False(result.Success);
        Assert.True(result.RequiresSecondFactor);
        Assert.Null(result.Error);
        Assert.Empty(await _blocks.ListAsync());
    }

    [Fact]
    public async Task SecondStep_WithTheRightCode_SignsIn_AndIsAudited()
    {
        var (secret, _) = await EnableForAnnaAsync();
        var pending = (await _guard.AttemptAsync("anna", Password, Client, Server)).Pending!;

        var result = await _guard.AttemptSecondFactorAsync(pending, CurrentCode(secret), Client, Server);

        Assert.True(result.Success);
        Assert.Equal(_anna.Id, AuthClaims.GetUserId(result.Principal!));
        Assert.Contains(AuditActions.Login, await AuditActionsAsync());
    }

    [Fact]
    public async Task SecondStep_WithAWrongCode_CountsAsAFailureForTheAddress()
    {
        await EnableForAnnaAsync();
        var pending = (await _guard.AttemptAsync("anna", Password, Client, Server)).Pending!;

        var result = await _guard.AttemptSecondFactorAsync(pending, "000000", Client, Server);

        Assert.False(result.Success);
        Assert.Equal(LoginService.WrongSecondFactor, result.Error);
        Assert.Equal(1, Assert.Single(await _blocks.ListAsync()).Failures);
    }

    [Fact]
    public async Task SecondStep_IsInvalid_AfterThePasswordChanged()
    {
        var (secret, _) = await EnableForAnnaAsync();
        var pending = (await _guard.AttemptAsync("anna", Password, Client, Server)).Pending!;
        await using (var db = _db.CreateDbContext())
            await db.Users.Where(u => u.Id == _anna.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, "neu"));

        var result = await _guard.AttemptSecondFactorAsync(pending, CurrentCode(secret), Client, Server);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Login_WithoutTwoFactor_IsUnchanged()
    {
        var result = await _guard.AttemptAsync("anna", Password, Client, Server);

        Assert.True(result.Success);
        Assert.False(result.RequiresSecondFactor);
    }
}
