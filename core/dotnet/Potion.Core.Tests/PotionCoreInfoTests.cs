using Potion.Core;
using Xunit;

public class PotionCoreInfoTests
{
    [Fact]
    public void VersionIsSemver()
    {
        var parts = PotionCoreInfo.Version.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _)));
    }

    [Fact]
    public void VersionMatchesSwiftCore()
    {
        // Both platform cores ship together, so their versions must agree.
        var swift = File.ReadAllText(Path.Combine(RepoRoot(), "core", "swift", "Sources", "PotionCore", "PotionCore.swift"));
        Assert.Contains($"version = \"{PotionCoreInfo.Version}\"", swift);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root (global.json) not found");
    }
}
