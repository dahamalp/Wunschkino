using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Wunschkino.Auth;
using Wunschkino.Data;
using Wunschkino.Hosting;
using Wunschkino.Tests.Helpers;

namespace Wunschkino.Tests.Hosting;

public class UserCommandsTests : IDisposable
{
    private const string NewPassword = "ganz-neues-passwort-123";

    private readonly TempDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly string _databasePath;

    public UserCommandsTests()
    {
        _databasePath = Path.Combine(_directory.Path, "wunschkino.db");
        using var db = CreateDb();
        db.Database.Migrate();
    }

    public void Dispose() => _directory.Dispose();

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=False").Options);

    private int AddUser(string name, UserRole role = UserRole.User, bool disabled = false, DateTime? lockedUntil = null, int failedLogins = 0)
    {
        using var db = CreateDb();
        var user = new User
        {
            Username = name,
            PasswordHash = "alter-hash",
            Role = role,
            IsDisabled = disabled,
            LockedUntilUtc = lockedUntil,
            FailedLogins = failedLogins
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    private User Reload(int id)
    {
        using var db = CreateDb();
        return db.Users.Single(u => u.Id == id);
    }

    private Task<int> RunAsync(FakePrompt prompt, params string[] args) =>
        UserCommands.RunAsync(args, _databasePath, prompt, _output, _error, _time, new PlainTextHasher());

    private string AllText => _output + _error.ToString();

    [Theory]
    [InlineData("--list-users", true)]
    [InlineData("--reset-password", true)]
    [InlineData("--unblock", false)]
    [InlineData("--port", false)]
    public void OnlyTheUserSwitches_AreRecognizedAsCommands(string arg, bool expected)
    {
        Assert.Equal(expected, UserCommands.IsUserCommand([arg]));
    }

    [Fact]
    public async Task List_ShowsEveryUserWithRoleAndState()
    {
        AddUser("anna", UserRole.Admin);
        AddUser("bert");
        AddUser("clara", disabled: true);
        AddUser("dora", lockedUntil: _time.GetUtcNow().UtcDateTime.AddMinutes(10));

        var exitCode = await RunAsync(new FakePrompt(), "--list-users");

        Assert.Equal(0, exitCode);
        var text = _output.ToString();
        Assert.Contains("anna", text);
        Assert.Contains("Admin", text);
        Assert.Contains("gesperrt (deaktiviert)", text);
        Assert.Contains("vorübergehend gesperrt bis 2026-10-04 12:10 UTC", text);
        Assert.Contains("aktiv", text);
        Assert.DoesNotContain("alter-hash", text);
    }

    [Fact]
    public async Task List_WithoutUsers_PointsToTheSetup()
    {
        var exitCode = await RunAsync(new FakePrompt(), "--list-users");

        Assert.Equal(0, exitCode);
        Assert.Contains("noch keine Benutzer", _output.ToString());
    }

    [Fact]
    public async Task ResetPassword_SetsTheNewPassword_LogsOutOldSessions_AndClearsTheLock()
    {
        var id = AddUser("anna", UserRole.Admin, lockedUntil: _time.GetUtcNow().UtcDateTime.AddHours(1), failedLogins: 4);
        var before = Reload(id);

        var exitCode = await RunAsync(new FakePrompt(NewPassword), "--reset-password", "anna");

        Assert.Equal(0, exitCode);
        var after = Reload(id);
        Assert.True(new PlainTextHasher().Verify(NewPassword, after.PasswordHash));
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(0, after.FailedLogins);
        Assert.Null(after.LockedUntilUtc);
        Assert.Contains("anna ist geändert", _output.ToString());
        Assert.Contains("abgemeldet", _output.ToString());
    }

    [Theory]
    [InlineData(null, "Konsole")]
    [InlineData("", "Konsole")]
    [InlineData("anna", "Konsole (anna)")]
    [InlineData("a.b_c-1", "Konsole (a.b_c-1)")]
    [InlineData("an na;'--", "Konsole (anna--)")]
    [InlineData("0123456789012345678901234", "Konsole (01234567890123456789)")]
    public void TheConsoleActor_NamesTheSudoUser_AfterCleaningIt(string? sudoUser, string expected) =>
        Assert.Equal(expected, BlockCommands.ConsoleActorFor(sudoUser));

    [Fact]
    public async Task ResetPassword_NamesTheOperator_InTheAuditLog()
    {
        AddUser("anna", UserRole.Admin);

        await UserCommands.RunAsync(["--reset-password", "anna"], _databasePath, new FakePrompt(NewPassword), _output, _error, _time,
            new PlainTextHasher(), operatorName: "bernd");

        await using var db = CreateDb();
        Assert.Equal("Konsole (bernd)", Assert.Single(db.AuditEntries).ActorName);
    }

    [Fact]
    public async Task ResetPassword_WarnsWhenTheAuditEntryCouldNotBeWritten_ButStillChangesThePassword()
    {
        AddUser("anna", UserRole.Admin);
        await using (var db = CreateDb())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AuditEntries");

        var exitCode = await RunAsync(new FakePrompt(NewPassword), "--reset-password", "anna");

        Assert.Equal(0, exitCode);
        Assert.Contains("nicht ins Protokoll", _error.ToString());
    }

    [Fact]
    public async Task ResetPassword_IsWrittenToTheAuditLog_AsKonsole_NeverWithThePassword()
    {
        AddUser("anna", UserRole.Admin);

        await RunAsync(new FakePrompt(NewPassword), "--reset-password", "anna");

        await using var db = CreateDb();
        var entry = Assert.Single(db.AuditEntries);
        Assert.Equal(AuditActions.UserPasswordReset, entry.Action);
        Assert.Equal(BlockCommands.ConsoleActor, entry.ActorName);
        Assert.Equal("anna", entry.Target);
        Assert.DoesNotContain(NewPassword, string.Join("|", entry.Action, entry.ActorName, entry.Target, entry.Detail, entry.Ip));
        Assert.DoesNotContain(NewPassword, AllText);
    }

    [Fact]
    public async Task ResetPassword_TheUsernameIsCaseInsensitive()
    {
        var id = AddUser("Anna");

        var exitCode = await RunAsync(new FakePrompt(NewPassword), "--reset-password", "ANNA");

        Assert.Equal(0, exitCode);
        Assert.True(new PlainTextHasher().Verify(NewPassword, Reload(id).PasswordHash));
    }

    [Fact]
    public async Task ResetPassword_ForAnUnknownUser_FailsWithoutAskingForAPassword()
    {
        var prompt = new FakePrompt(NewPassword);

        var exitCode = await RunAsync(prompt, "--reset-password", "gibtsnicht");

        Assert.Equal(1, exitCode);
        Assert.Contains("gibt es nicht", _error.ToString());
        Assert.Equal(0, prompt.Reads);
    }

    [Theory]
    [InlineData]
    [InlineData("--andere-option")]
    public async Task ResetPassword_WithoutAUsername_ShowsTheUsage(params string[] following)
    {
        var exitCode = await RunAsync(new FakePrompt(NewPassword), ["--reset-password", .. following]);

        Assert.Equal(2, exitCode);
        Assert.Contains("--reset-password <Benutzer>", _error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("kurz")]
    [InlineData("elf-zeichen")]
    public async Task ResetPassword_WithAnInvalidPassword_ChangesNothing(string typed)
    {
        var id = AddUser("anna");

        var exitCode = await RunAsync(new FakePrompt(typed), "--reset-password", "anna");

        Assert.Equal(1, exitCode);
        Assert.Equal("alter-hash", Reload(id).PasswordHash);
        await using var db = CreateDb();
        Assert.Empty(db.AuditEntries);
    }

    [Fact]
    public async Task ResetPassword_WithNoInputAtAll_ChangesNothing()
    {
        var id = AddUser("anna");

        var exitCode = await RunAsync(new FakePrompt(), "--reset-password", "anna");

        Assert.Equal(1, exitCode);
        Assert.Equal("alter-hash", Reload(id).PasswordHash);
    }

    [Fact]
    public async Task ResetPassword_AtTheTerminal_AsksTwice_AndRefusesADifferentRepeat()
    {
        var id = AddUser("anna");

        var mismatch = await RunAsync(new FakePrompt(true, NewPassword, "ein-anderes-passwort-999"), "--reset-password", "anna");
        Assert.Equal(1, mismatch);
        Assert.Contains("stimmen nicht überein", _error.ToString());
        Assert.Equal("alter-hash", Reload(id).PasswordHash);

        var ok = await RunAsync(new FakePrompt(true, NewPassword, NewPassword), "--reset-password", "anna");
        Assert.Equal(0, ok);
        Assert.True(new PlainTextHasher().Verify(NewPassword, Reload(id).PasswordHash));
    }

    [Fact]
    public async Task ResetPassword_FromAPipe_AsksOnce()
    {
        AddUser("anna");
        var prompt = new FakePrompt(NewPassword);

        await RunAsync(prompt, "--reset-password", "anna");

        Assert.Equal(1, prompt.Reads);
    }

    [Fact]
    public async Task ResetPassword_ForADisabledAccount_WorksButSaysTheAccountStaysBlocked()
    {
        var id = AddUser("anna", disabled: true);

        var exitCode = await RunAsync(new FakePrompt(NewPassword), "--reset-password", "anna");

        Assert.Equal(0, exitCode);
        Assert.True(Reload(id).IsDisabled);
        Assert.Contains("gesperrt (deaktiviert)", _output.ToString());
    }

    [Fact]
    public async Task ResetPassword_OnlyChangesTheNamedUser()
    {
        var anna = AddUser("anna");
        var bert = AddUser("bert");

        await RunAsync(new FakePrompt(NewPassword), "--reset-password", "anna");

        Assert.Equal("alter-hash", Reload(bert).PasswordHash);
        Assert.NotEqual("alter-hash", Reload(anna).PasswordHash);
    }

    [Fact]
    public async Task AMissingDatabase_IsReported_WithoutCreatingOne()
    {
        var path = Path.Combine(_directory.Path, "gibtsnicht", "wunschkino.db");

        var exitCode = await UserCommands.RunAsync(["--list-users"], path, new FakePrompt(), _output, _error, _time);

        Assert.Equal(1, exitCode);
        Assert.Contains("Keine Datenbank", _error.ToString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AnOldDatabaseWithoutTheTables_ExplainsWhatToDo()
    {
        var oldPath = Path.Combine(_directory.Path, "alt.db");
        await using (var connection = new SqliteConnection($"Data Source={oldPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Etwas (Id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var exitCode = await UserCommands.RunAsync(["--list-users"], oldPath, new FakePrompt(), _output, _error, _time);

        Assert.Equal(1, exitCode);
        Assert.Contains("Starte die App einmal", _error.ToString());
    }

    [Fact]
    public async Task ResetTwoFactor_TurnsItOff_SignsOutAndIsAudited()
    {
        var id = AddUser("anna");
        using (var db = CreateDb())
        {
            var user = db.Users.Single(u => u.Id == id);
            user.TotpSecret = "verschluesselt";
            user.TotpEnabledUtc = DateTime.UtcNow;
            db.RecoveryCodes.Add(new RecoveryCode { UserId = id, CodeHash = new string('a', 64) });
            db.SaveChanges();
        }
        var stampBefore = Reload(id).SecurityStamp;

        var exitCode = await RunAsync(new FakePrompt(), "--reset-2fa", "anna");

        Assert.Equal(0, exitCode);
        var after = Reload(id);
        Assert.False(after.HasTwoFactor);
        Assert.Null(after.TotpSecret);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
        using var check = CreateDb();
        Assert.Empty(check.RecoveryCodes);
        Assert.Equal(AuditActions.TwoFactorReset, check.AuditEntries.Single().Action);
    }

    [Fact]
    public async Task ResetTwoFactor_WithoutTwoFactor_ChangesNothing()
    {
        AddUser("anna");

        var exitCode = await RunAsync(new FakePrompt(), "--reset-2fa", "anna");

        Assert.Equal(0, exitCode);
        Assert.Contains("keine Zwei-Faktor-Anmeldung", AllText);
    }

    [Fact]
    public async Task ResetTwoFactor_WithoutUsername_ShowsUsage()
    {
        var exitCode = await RunAsync(new FakePrompt(), "--reset-2fa");

        Assert.Equal(2, exitCode);
        Assert.Contains("--reset-2fa", _error.ToString());
    }

    private sealed class FakePrompt : IPasswordPrompt
    {
        private readonly Queue<string?> _answers;

        public FakePrompt(params string?[] answers)
            : this(false, answers)
        {
        }

        public FakePrompt(bool interactive, params string?[] answers)
        {
            IsInteractive = interactive;
            _answers = new Queue<string?>(answers);
        }

        public bool IsInteractive { get; }

        public int Reads { get; private set; }

        public string? Read(string label)
        {
            Reads++;
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }
}
