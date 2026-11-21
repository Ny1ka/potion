/// <summary>Locates files in the repository (shared spec, design system) from tests.</summary>
internal static class RepoFiles
{
    public static readonly string Root = FindRoot();

    public static string Path(string relative) => System.IO.Path.Combine(Root, relative);
    public static string Text(string relative) => File.ReadAllText(Path(relative));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root (global.json) not found");
    }
}
