using System.Text.RegularExpressions;

namespace SharpBench;

/// <summary>
/// Maps gating test fully-qualified names (and the dataset test patch) to the test .csproj files
/// that actually contain them. Avoids running every test project in large monorepos.
/// </summary>
internal static partial class TestProjectResolver
{
    [GeneratedRegex(@"^\+\+\+ b/(.+)$", RegexOptions.Multiline)]
    private static partial Regex PatchPathRe();

    /// <summary>
    /// Returns repo-relative paths to test .csproj files, or empty when nothing could be resolved
    /// (caller falls back to scanning all test projects).
    /// </summary>
    public static List<string> Resolve(string srcDir, IEnumerable<string> testNames, string? testPatch = null)
    {
        var names = testNames.Distinct(StringComparer.Ordinal).ToList();
        var classNames = names.Select(ExtractClassName).Where(c => c is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (TryProjectFromNamespaceRoot(srcDir, name) is { } nsProj)
                projects.Add(nsProj);

            if (ExtractClassName(name) is not { } className) continue;
            foreach (var proj in FindProjectsForClass(srcDir, className, name))
                projects.Add(proj);
        }

        // Patch paths are a fallback when fixture classes cannot be located (e.g. new files).
        if (projects.Count == 0 && !string.IsNullOrWhiteSpace(testPatch))
        {
            foreach (var path in ExtractPatchPaths(testPatch))
            {
                var fileClass = Path.GetFileNameWithoutExtension(path);
                if (fileClass is null || !classNames.Contains(fileClass)) continue;
                if (TryProjectFromSourcePath(srcDir, path) is { } proj)
                    projects.Add(proj);
            }
        }

        return projects
            .Where(p => !TestProjectFilter.IsExcluded(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Maps each gating test to the project(s) that contain it.</summary>
    public static Dictionary<string, List<string>> GroupByProject(
        string srcDir, IEnumerable<string> testNames, string? testPatch = null)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in testNames.Distinct(StringComparer.Ordinal))
        {
            foreach (var proj in Resolve(srcDir, new[] { name }, testPatch))
            {
                if (!groups.TryGetValue(proj, out var list))
                    groups[proj] = list = new List<string>();
                list.Add(name);
            }
        }
        return groups;
    }

    /// <summary>
    /// e.g. <c>Avalonia.Controls.UnitTests.FlyoutTests.Method</c> →
    /// <c>tests/Avalonia.Controls.UnitTests/Avalonia.Controls.UnitTests.csproj</c>
    /// </summary>
    private static string? TryProjectFromNamespaceRoot(string srcDir, string fullyQualifiedName)
    {
        var parts = fullyQualifiedName.Split('.');
        if (parts.Length < 3) return null;

        for (var len = parts.Length - 2; len >= 2; len--)
        {
            var candidate = string.Join('.', parts[..len]);
            foreach (var root in new[] { "test", "tests" })
            {
                var proj = $"{root}/{candidate}/{candidate}.csproj";
                if (File.Exists(Path.Combine(srcDir, proj)) && !TestProjectFilter.IsExcluded(proj))
                    return proj;
            }
        }
        return null;
    }

    private static IEnumerable<string> ExtractPatchPaths(string patch) =>
        PatchPathRe().Matches(patch).Select(m => m.Groups[1].Value.Trim())
            .Where(p => p != "/dev/null");

    private static string? ExtractClassName(string fullyQualifiedTestName)
    {
        var parts = fullyQualifiedTestName.Split('.');
        return parts.Length >= 2 ? parts[^2] : null;
    }

    private static string? TryProjectFromSourcePath(string srcDir, string repoRelativePath)
    {
        var normalized = repoRelativePath.Replace('\\', '/');
        if (!normalized.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && !normalized.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
            return null;

        var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
        while (!string.IsNullOrEmpty(dir))
        {
            var csproj = $"{dir}/{Path.GetFileName(dir)}.csproj";
            if (File.Exists(Path.Combine(srcDir, csproj)))
                return csproj;
            var parent = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static IEnumerable<string> FindProjectsForClass(string srcDir, string className, string fullyQualifiedName)
    {
        var candidates = new List<(string Proj, int Score)>();

        foreach (var root in new[] { "test", "tests" })
        {
            var rootPath = Path.Combine(srcDir, root);
            if (!Directory.Exists(rootPath)) continue;

            foreach (var file in Directory.EnumerateFiles(rootPath, className + ".cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                var rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (TryProjectFromSourcePath(srcDir, rel) is not { } proj
                    || TestProjectFilter.IsExcluded(proj))
                    continue;

                candidates.Add((proj, ScoreProject(proj, rel, fullyQualifiedName)));
            }
        }

        if (candidates.Count == 0) yield break;

        var best = candidates.Max(c => c.Score);
        foreach (var proj in candidates.Where(c => c.Score == best).Select(c => c.Proj).Distinct())
            yield return proj;
    }

    /// <summary>Higher is better when multiple projects contain the same fixture class name.</summary>
    private static int ScoreProject(string proj, string sourceRel, string fullyQualifiedName)
    {
        var score = 0;
        var nsPrefix = fullyQualifiedName.Contains('.')
            ? fullyQualifiedName[..fullyQualifiedName.LastIndexOf('.')]
            : fullyQualifiedName;

        if (sourceRel.Contains(nsPrefix.Replace('.', '/'), StringComparison.Ordinal))
            score += 40;
        if (proj.Contains("UnitTests", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (proj.Contains("Integration", StringComparison.OrdinalIgnoreCase)) score -= 10;
        if (proj.Contains("FunctionalTests", StringComparison.OrdinalIgnoreCase)) score -= 5;
        if (Path.GetFileNameWithoutExtension(proj) is { } name
            && nsPrefix.Contains(name, StringComparison.Ordinal))
            score += 30;
        score -= proj.Count(c => c == '/'); // prefer shallower / more specific projects
        return score;
    }
}
