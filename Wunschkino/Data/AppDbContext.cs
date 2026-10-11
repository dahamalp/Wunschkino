using Microsoft.EntityFrameworkCore;

namespace Wunschkino.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Wish> Wishes => Set<Wish>();
    public DbSet<WishVote> WishVotes => Set<WishVote>();
    public DbSet<SeriesGap> SeriesGaps => Set<SeriesGap>();
    public DbSet<IpBlock> IpBlocks => Set<IpBlock>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<WishAction> WishActions => Set<WishAction>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WishAction>(action =>
        {
            // Das Tageslimit zählt die Handlungen eines Nutzers im Zeitfenster, das Aufräumen sucht die ältesten.
            action.HasIndex(a => new { a.UserId, a.TimeUtc });
            action.HasIndex(a => a.TimeUtc);
        });

        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.Property(e => e.ActorName).HasMaxLength(32);
            entry.Property(e => e.Action).HasMaxLength(48);
            entry.Property(e => e.Target).HasMaxLength(100);
            entry.Property(e => e.Detail).HasMaxLength(300);
            entry.Property(e => e.Ip).HasMaxLength(64);
            // Die Liste zeigt die neuesten Einträge, das Aufräumen sucht die ältesten.
            entry.HasIndex(e => e.TimeUtc);
        });

        modelBuilder.Entity<IpBlock>(block =>
        {
            block.Property(b => b.Address).HasMaxLength(64);
            block.HasIndex(b => b.Address).IsUnique();
        });

        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Username).HasMaxLength(32).UseCollation("NOCASE");
            user.HasIndex(u => u.Username).IsUnique();
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
            user.Property(u => u.ThemePreference).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ThemePreference.System);
            user.Property(u => u.TotpSecret).HasMaxLength(512);
            user.Property(u => u.Language).HasMaxLength(8);
            user.Ignore(u => u.HasTwoFactor);
        });

        modelBuilder.Entity<RecoveryCode>(code =>
        {
            code.Property(c => c.CodeHash).HasMaxLength(256);
            code.HasIndex(c => new { c.UserId, c.CodeHash }).IsUnique();
            code.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Setting>(setting =>
        {
            setting.HasKey(s => s.Key);
            setting.Property(s => s.Key).HasMaxLength(64);
        });

        modelBuilder.Entity<Wish>(wish =>
        {
            wish.Property(w => w.Kind).HasConversion<string>().HasMaxLength(16);
            wish.Property(w => w.Status).HasConversion<string>().HasMaxLength(16);
            wish.Property(w => w.AutoCompletion).HasConversion<string>().HasMaxLength(16);
            wish.Property(w => w.Title).HasMaxLength(300);
            wish.Property(w => w.PosterPath).HasMaxLength(100);
            wish.Property(w => w.ImdbId).HasMaxLength(16);
            wish.Property(w => w.AdminComment).HasMaxLength(500);
            // Die Eindeutigkeit sichert die DB, damit zwei gleichzeitige Wünsche nicht beide durchkommen.
            wish.HasIndex(w => new { w.TmdbId, w.Kind }).IsUnique();
            wish.HasOne<User>().WithMany().HasForeignKey(w => w.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SeriesGap>(gap =>
        {
            gap.Property(g => g.JellyfinId).HasMaxLength(64);
            gap.Property(g => g.Title).HasMaxLength(300);
            gap.Property(g => g.Status).HasConversion<string>().HasMaxLength(24);
            gap.HasIndex(g => g.JellyfinId).IsUnique();
        });

        modelBuilder.Entity<WishVote>(vote =>
        {
            vote.HasKey(v => new { v.WishId, v.UserId });
            vote.Property(v => v.Note).HasMaxLength(300);
            vote.HasOne(v => v.Wish).WithMany(w => w.Votes).HasForeignKey(v => v.WishId).OnDelete(DeleteBehavior.Cascade);
            vote.HasOne(v => v.User).WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
