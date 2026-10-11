using Wunschkino.DatabaseBackup;

namespace Wunschkino.Tests.DatabaseBackup;

public class MountPointsTests
{
    private static readonly string[] Mounts = ["/", "/var/lib", "/mnt/sicherung", "/mnt/sicherung/alt", "/boot"];

    [Theory]
    [InlineData("/var/lib/wunschkino", "/etc/wunschkino", false)]
    [InlineData("/var/lib/wunschkino", "/var/lib/wunschkino-backups", true)]
    [InlineData("/var/lib/wunschkino", "/mnt/sicherung/wunschkino", false)]
    [InlineData("/mnt/sicherung/wunschkino", "/mnt/sicherung/alt/x", false)]
    [InlineData("/mnt/sicherung/a", "/mnt/sicherung/b", true)]
    [InlineData("/opt/wunschkino", "/etc/wunschkino", true)]
    [InlineData("/var/lib/wunschkino/", "/var/lib/wunschkino/backups/", true)]
    public void OnSameMount_ComparesTheMountEachPathBelongsTo(string a, string b, bool expected)
    {
        Assert.Equal(expected, MountPoints.OnSameMount(a, b, Mounts));
    }

    [Fact]
    public void OwnerOf_PicksTheLongestMatchingMount_NotJustAPrefixOfTheName()
    {
        Assert.Equal("/var/lib", MountPoints.OwnerOf("/var/lib/wunschkino", Mounts));
        Assert.Equal("/mnt/sicherung/alt", MountPoints.OwnerOf("/mnt/sicherung/alt/2026", Mounts));
        // „/var/library“ beginnt mit „/var/lib“, liegt aber nicht darin.
        Assert.Equal("/", MountPoints.OwnerOf("/var/library/x", Mounts));
    }

    [Fact]
    public void ParseProcMounts_TakesTheMountPointColumn_AndDecodesSpaces()
    {
        var lines = new[]
        {
            "/dev/sda1 / ext4 rw,relatime 0 0",
            "tmpfs /run tmpfs rw,nosuid 0 0",
            "/dev/sdb1 /mnt/meine\\040platte ext4 rw 0 0",
            "",
            "kaputt"
        };

        Assert.Equal(["/", "/run", "/mnt/meine platte"], MountPoints.ParseProcMounts(lines));
    }

    [Fact]
    public void WithoutAnyKnownMounts_EverythingCountsAsOneMount_SoTheHintErrsOnTheSafeSide()
    {
        Assert.True(MountPoints.OnSameMount("/a", "/b", []));
    }

    [Fact]
    public void ReadMountPoints_ReturnsAList_AndNeverThrows()
    {
        var mounts = MountPoints.ReadMountPoints();

        Assert.NotNull(mounts);
    }
}
