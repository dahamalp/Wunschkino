using System.Security.Claims;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Auth;

public class PrincipalValidatorTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly PrincipalValidator _validator;

    public PrincipalValidatorTests() => _validator = new PrincipalValidator(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CurrentPrincipal_IsValid()
    {
        var user = TestUsers.Add(_db, "anna");

        Assert.True(await _validator.IsValidAsync(AuthClaims.CreatePrincipal(user)));
    }

    [Fact]
    public async Task ChangedSecurityStamp_InvalidatesPrincipal()
    {
        var user = TestUsers.Add(_db, "anna");
        var principal = AuthClaims.CreatePrincipal(user);
        Update(user.Id, u => u.SecurityStamp = User.NewSecurityStamp());

        Assert.False(await _validator.IsValidAsync(principal));
    }

    [Fact]
    public async Task DisabledUser_InvalidatesPrincipal()
    {
        var user = TestUsers.Add(_db, "anna");
        var principal = AuthClaims.CreatePrincipal(user);
        Update(user.Id, u => u.IsDisabled = true);

        Assert.False(await _validator.IsValidAsync(principal));
    }

    [Fact]
    public async Task ChangedRole_InvalidatesPrincipal()
    {
        var user = TestUsers.Add(_db, "anna", UserRole.Admin);
        var principal = AuthClaims.CreatePrincipal(user);
        Update(user.Id, u => u.Role = UserRole.User);

        Assert.False(await _validator.IsValidAsync(principal));
    }

    [Fact]
    public async Task DeletedUserOrMissingClaims_AreInvalid()
    {
        Assert.False(await _validator.IsValidAsync(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(await _validator.IsValidAsync(AuthClaims.CreatePrincipal(new User { Id = 999, Username = "weg", PasswordHash = "x" })));
    }

    private void Update(int id, Action<User> change)
    {
        using var db = _db.CreateDbContext();
        change(db.Users.Single(u => u.Id == id));
        db.SaveChanges();
    }
}
