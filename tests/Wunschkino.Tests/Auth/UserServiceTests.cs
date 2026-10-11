using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class UserServiceTests : IDisposable
{
    private const string Password = "ein-sicheres-passwort";

    private readonly TestDb _db = new();
    private readonly UserService _users;
    private readonly User _admin;

    public UserServiceTests()
    {
        _users = new UserService(_db, new PlainTextHasher(), TimeProvider.System);
        _admin = TestUsers.Add(_db, "admin", UserRole.Admin);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Create_AddsUserWithHashedPassword()
    {
        var result = await _users.CreateAsync(_admin.Id, "anna", Password, UserRole.User);

        Assert.True(result.Success, result.Error);
        var list = await _users.ListAsync();
        Assert.Contains(list, u => u.Username == "anna" && u.Role == UserRole.User);
    }

    [Fact]
    public async Task ConfirmPassword_AcceptsOnlyTheCurrentPassword()
    {
        Assert.True((await _users.ConfirmPasswordAsync(_admin.Id, Password)).Success);
        Assert.False((await _users.ConfirmPasswordAsync(_admin.Id, "falsch")).Success);
        Assert.False((await _users.ConfirmPasswordAsync(_admin.Id, "")).Success);
        Assert.False((await _users.ConfirmPasswordAsync(_admin.Id, null)).Success);
        Assert.False((await _users.ConfirmPasswordAsync(_admin.Id, new string('x', UserValidation.MaxPasswordLength + 1))).Success);
    }

    [Fact]
    public async Task ConfirmPassword_RefusesUnknownAndDisabledAccounts()
    {
        var gone = TestUsers.Add(_db, "weg", disabled: true);

        Assert.False((await _users.ConfirmPasswordAsync(gone.Id, Password)).Success);
        Assert.False((await _users.ConfirmPasswordAsync(9999, Password)).Success);
    }

    [Fact]
    public async Task ConfirmPassword_LocksAfterTooManyWrongTries_LikeTheLogin()
    {
        for (var i = 0; i < 10; i++)
            await _users.ConfirmPasswordAsync(_admin.Id, "falsch");

        Assert.False((await _users.ConfirmPasswordAsync(_admin.Id, Password)).Success);
    }

    [Theory]
    [InlineData("ab", Password)]
    [InlineData("anna", "kurz")]
    [InlineData("ADMIN", Password)]
    public async Task Create_RejectsInvalidOrDuplicateInput(string username, string password)
    {
        var result = await _users.CreateAsync(_admin.Id, username, password, UserRole.User);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AdminActions_RequireActiveAdmin()
    {
        var normal = TestUsers.Add(_db, "bob");
        var disabledAdmin = TestUsers.Add(_db, "ex-admin", UserRole.Admin, disabled: true);

        Assert.False((await _users.CreateAsync(normal.Id, "anna", Password, UserRole.Admin)).Success);
        Assert.False((await _users.SetRoleAsync(normal.Id, normal.Id, UserRole.Admin)).Success);
        Assert.False((await _users.ResetPasswordAsync(disabledAdmin.Id, _admin.Id, "neues-langes-passwort")).Success);
        Assert.Equal(UserRole.User, TestUsers.Reload(_db, normal.Id).Role);
    }

    [Fact]
    public async Task ResetPassword_SetsHashUnlocksAndRotatesStamp()
    {
        var anna = TestUsers.Add(_db, "anna");
        using (var db = _db.CreateDbContext())
        {
            var tracked = db.Users.Single(u => u.Id == anna.Id);
            tracked.FailedLogins = 3;
            tracked.LockedUntilUtc = DateTime.UtcNow.AddMinutes(10);
            db.SaveChanges();
        }

        var result = await _users.ResetPasswordAsync(_admin.Id, anna.Id, "neues-langes-passwort");

        Assert.True(result.Success, result.Error);
        var reloaded = TestUsers.Reload(_db, anna.Id);
        Assert.Equal("hashed:neues-langes-passwort", reloaded.PasswordHash);
        Assert.Null(reloaded.LockedUntilUtc);
        Assert.Equal(0, reloaded.FailedLogins);
        Assert.NotEqual(anna.SecurityStamp, reloaded.SecurityStamp);
    }

    [Fact]
    public async Task LastActiveAdmin_CannotBeDemoted()
    {
        var other = TestUsers.Add(_db, "zweiter", UserRole.Admin);

        Assert.True((await _users.SetRoleAsync(_admin.Id, other.Id, UserRole.User)).Success);
        Assert.False((await _users.SetRoleAsync(_admin.Id, _admin.Id, UserRole.User)).Success);
        Assert.Equal(UserRole.Admin, TestUsers.Reload(_db, _admin.Id).Role);
    }

    [Fact]
    public async Task OwnAccount_CannotBeDisabled()
    {
        TestUsers.Add(_db, "zweiter", UserRole.Admin);

        var result = await _users.SetDisabledAsync(_admin.Id, _admin.Id, disabled: true);

        Assert.False(result.Success);
        Assert.False(TestUsers.Reload(_db, _admin.Id).IsDisabled);
    }

    [Fact]
    public async Task SetDisabled_DisablesOtherUserAndRotatesStamp()
    {
        var anna = TestUsers.Add(_db, "anna");

        Assert.True((await _users.SetDisabledAsync(_admin.Id, anna.Id, disabled: true)).Success);

        var reloaded = TestUsers.Reload(_db, anna.Id);
        Assert.True(reloaded.IsDisabled);
        Assert.NotEqual(anna.SecurityStamp, reloaded.SecurityStamp);
    }

    [Fact]
    public async Task ChangeOwnPassword_LocksAfterTooManyWrongCurrentPasswords()
    {
        var anna = TestUsers.Add(_db, "anna");
        for (var i = 0; i < LoginThrottle.MaxFailedAttempts; i++)
            await _users.ChangeOwnPasswordAsync(anna.Id, "falsch", "neues-langes-passwort");

        var withCorrectPassword = await _users.ChangeOwnPasswordAsync(anna.Id, Password, "neues-langes-passwort");

        Assert.False(withCorrectPassword.Success);
        Assert.Equal("hashed:" + Password, TestUsers.Reload(_db, anna.Id).PasswordHash);
    }

    [Fact]
    public async Task ParallelDemotions_LeaveAtLeastOneActiveAdmin()
    {
        var other = TestUsers.Add(_db, "zweiter", UserRole.Admin);

        await Task.WhenAll(
            Task.Run(() => _users.SetRoleAsync(_admin.Id, other.Id, UserRole.User)),
            Task.Run(() => _users.SetRoleAsync(other.Id, _admin.Id, UserRole.User)));

        using var db = _db.CreateDbContext();
        Assert.Equal(1, db.Users.Count(u => u.Role == UserRole.Admin && !u.IsDisabled));
    }

    [Fact]
    public async Task Create_RejectsOverlongPassword()
    {
        var result = await _users.CreateAsync(_admin.Id, "anna", new string('x', UserValidation.MaxPasswordLength + 1), UserRole.User);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ChangeOwnPassword_RequiresCurrentPassword()
    {
        var anna = TestUsers.Add(_db, "anna");

        Assert.False((await _users.ChangeOwnPasswordAsync(anna.Id, "falsch", "neues-langes-passwort")).Success);
        Assert.True((await _users.ChangeOwnPasswordAsync(anna.Id, Password, "neues-langes-passwort")).Success);

        var reloaded = TestUsers.Reload(_db, anna.Id);
        Assert.Equal("hashed:neues-langes-passwort", reloaded.PasswordHash);
        Assert.NotEqual(anna.SecurityStamp, reloaded.SecurityStamp);
    }
}
