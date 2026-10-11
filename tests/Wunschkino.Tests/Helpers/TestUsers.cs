using Wunschkino.Data;

namespace Wunschkino.Tests.Helpers;

public static class TestUsers
{
    public static User Add(TestDb testDb, string username, UserRole role = UserRole.User, string password = "ein-sicheres-passwort", bool disabled = false)
    {
        using var db = testDb.CreateDbContext();
        var user = new User
        {
            Username = username,
            PasswordHash = new PlainTextHasher().Hash(password),
            Role = role,
            IsDisabled = disabled
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    public static User Reload(TestDb testDb, int id)
    {
        using var db = testDb.CreateDbContext();
        return db.Users.Single(u => u.Id == id);
    }
}
