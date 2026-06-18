namespace SharpBench;

/// <summary>
/// Nerdbank.GitVersioning needs git history beyond a depth-1 clone. Detect repos that use it
/// so we can deepen the fetch before running tests in Docker.
/// </summary>
internal static class NerdbankGitVersioning
{
    /// <summary>History depth that covers typical version-height resets without a full unshallow.</summary>
    public const int FetchDepth = 2000;

    public static bool IsEnabled(string srcDir) =>
        File.Exists(Path.Combine(srcDir, "version.json"))
        || ContainsPackageReference(srcDir, "Directory.Packages.props")
        || ContainsPackageReference(srcDir, "Directory.Build.props");

    private static bool ContainsPackageReference(string srcDir, string fileName)
    {
        foreach (var path in Directory.EnumerateFiles(srcDir, fileName, SearchOption.AllDirectories))
        {
            try
            {
                if (File.ReadAllText(path).Contains("Nerdbank.GitVersioning", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* ignore */ }
        }
        return false;
    }
}
