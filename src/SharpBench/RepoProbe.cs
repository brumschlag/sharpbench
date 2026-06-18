using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpBench;

/// <summary>
/// Inspects a checked-out repo to decide which .NET SDK image and target framework to run tests under.
/// The framework major dictates the image, because the runtime for that TFM must be present.
/// </summary>
public static partial class RepoProbe
{
    public sealed record Plan(string SdkImage, string Framework, int SdkMajorFromGlobalJson);

    // net6.0 / net6 (not net48, net472, netstandard2.0). Optional platform suffix ignored for Linux Docker.
    [GeneratedRegex(@"\bnet((?:[5-9]|1[0-9]))(\.0)?(?:-(?:windows|android|ios|browser|macos)\b[^\s;""]*)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex NetCoreTfm();

    [GeneratedRegex(@"\bnetcoreapp(\d+)\.(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NetCoreAppTfm();

    public static Plan Detect(string srcDir) =>
        Detect(srcDir, repoRelativeProjects: null);

    /// <summary>Pick SDK/framework from specific test projects (after <see cref="TestProjectResolver"/>).</summary>
    public static Plan Detect(string srcDir, IReadOnlyList<string>? repoRelativeProjects)
    {
        int? globalMajor = ReadGlobalJsonMajor(srcDir);

        HashSet<string> testTfms;
        if (repoRelativeProjects is { Count: > 0 })
        {
            testTfms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in repoRelativeProjects)
            {
                var full = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                var fromProject = ProjectTfms(srcDir, full).ToList();
                if (fromProject.Count > 0)
                {
                    testTfms.UnionWith(fromProject);
                    continue;
                }

                testTfms.UnionWith(ImportedBuildTfms(srcDir, full));

                var paths = new List<string> { full };
                for (var dir = Path.GetDirectoryName(full); dir is not null && dir.StartsWith(srcDir, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir))
                {
                    var props = Path.Combine(dir, "Directory.Build.props");
                    if (File.Exists(props)) paths.Add(props);
                }
                testTfms.UnionWith(CollectCoreAppTfms(paths));
            }
        }
        else
            testTfms = CollectCoreAppTfms(TestTfmSources(srcDir)).ToHashSet();

        if (testTfms.Count == 0)
        {
            if (repoRelativeProjects is { Count: > 0 })
            {
                foreach (var rel in repoRelativeProjects)
                {
                    var full = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    if (ResolveDefaultNetCoreTargetFramework(srcDir, full) is { } defaultTfm)
                        testTfms.Add(defaultTfm);
                }
            }
            else if (ResolveDefaultNetCoreTargetFramework(srcDir) is { } defaultTfm)
                testTfms.Add(defaultTfm);
        }

        if (testTfms.Count == 0)
            testTfms = CollectCoreAppTfms(TestProjectFiles(srcDir)).ToHashSet();

        var majors = testTfms.Select(TfmSdkMajor).ToHashSet();
        int fwMajor;
        if (globalMajor is int g && majors.Contains(g)) fwMajor = g;
        else if (majors.Count > 0) fwMajor = majors.Max();
        else fwMajor = globalMajor ?? 8;

        var framework = PickFramework(testTfms, fwMajor);
        int sdkMajor = SdkMajorForFramework(framework, globalMajor, fwMajor);
        sdkMajor = Math.Max(sdkMajor, LangVersionSdkFloor(srcDir));
        if (SixLaborsBuildHelper.UsesPreviewBuild(srcDir))
            sdkMajor = Math.Max(sdkMajor, SixLaborsBuildHelper.PreviewSdkMajorFloor);

        return new Plan(
            SdkImage: SdkImage(sdkMajor, globalMajor, ReadGlobalJsonVersion(srcDir)),
            Framework: framework,
            SdkMajorFromGlobalJson: globalMajor ?? -1);
    }

    /// <summary>
    /// Pin SDK patch images when global.json requests a 9.0 preview/rc — the rolling <c>sdk:9.0</c>
    /// tag ships a compiler that breaks older EF Core test code (CS0023 on <c>Enumerable.Reverse</c>).
    /// </summary>
    private static string SdkImage(int sdkMajor, int? globalMajor, string? globalVersion)
    {
        if (sdkMajor >= 9 || globalMajor is >= 9)
        {
            if (globalVersion is not null)
            {
                var prefix = globalVersion.Split('-')[0];
                if (Version.TryParse(prefix, out var v) && v is { Major: 9, Minor: 0, Build: 100 })
                    return "mcr.microsoft.com/dotnet/sdk:9.0.101";
                if (prefix.Count(c => c == '.') >= 2)
                    return $"mcr.microsoft.com/dotnet/sdk:{prefix}";
            }
            return "mcr.microsoft.com/dotnet/sdk:9.0.101";
        }
        return $"mcr.microsoft.com/dotnet/sdk:{sdkMajor}.0";
    }

    private static string? ReadGlobalJsonVersion(string srcDir)
    {
        var gj = Directory.EnumerateFiles(srcDir, "global.json", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
        if (gj is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(SafeRead(gj));
            if (doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                sdk.TryGetProperty("version", out var ver))
                return ver.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    /// <summary>
    /// Prefer the exact moniker the repo uses (e.g. <c>net6</c> vs <c>net6.0</c>, <c>netcoreapp3.1</c>).
    /// </summary>
    private static string PickFramework(IEnumerable<string> tfms, int major)
    {
        var runnable = OperatingSystem.IsLinux()
            ? tfms.Where(t => !t.StartsWith("net4", StringComparison.OrdinalIgnoreCase)).ToList()
            : tfms.ToList();
        if (runnable.Count == 0) runnable = tfms.ToList();

        var netCoreApp = runnable
            .Where(t => t.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(TfmSortKey)
            .FirstOrDefault();

        var modern = runnable
            .Where(t => t.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                        && !t.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
                        && !t.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(TfmSortKey)
            .ToList();
        if (modern.Count > 0)
        {
            var forMajor = modern.Where(t => TfmSdkMajor(t) == major).ToList();
            if (forMajor.Count > 0) return forMajor[0];
            return modern[0];
        }

        if (netCoreApp is not null) return netCoreApp;

        var forMajorSet = runnable.Where(t => TfmSdkMajor(t) == major).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shortForm = $"net{major}";
        if (forMajorSet.Contains(shortForm)) return shortForm;
        if (forMajorSet.Contains($"{shortForm}.0")) return $"{shortForm}.0";
        return runnable.OrderByDescending(TfmSortKey).FirstOrDefault() ?? $"{shortForm}.0";
    }

    [GeneratedRegex(@"<LangVersion>\s*(\d+(?:\.\d+)?)\s*</LangVersion>", RegexOptions.IgnoreCase)]
    private static partial Regex LangVersionRe();

    /// <summary>Minimum SDK major required to compile the repo's <c>LangVersion</c>.</summary>
    private static int LangVersionSdkFloor(string srcDir)
    {
        int floor = 0;
        foreach (var props in Directory.EnumerateFiles(srcDir, "Directory.Build.props", SearchOption.AllDirectories))
        {
            var m = LangVersionRe().Match(SafeRead(props));
            if (!m.Success) continue;
            var ver = m.Groups[1].Value;
            if (!int.TryParse(ver.Split('.')[0], out var major)) continue;
            floor = Math.Max(floor, major switch
            {
                >= 11 => 8,
                10 => 6,
                9 => 5,
                _ => 0,
            });
        }
        return floor;
    }

    private static int SdkMajorForFramework(string framework, int? globalMajor, int fwMajor)
    {
        int floor = framework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) ? 8 : fwMajor;
        if (globalMajor is int g) return Math.Max(g, floor);
        return Math.Max(floor, 6);
    }

    private static (int Major, int Minor) TfmSortKey(string tfm)
    {
        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            var m = NetCoreAppTfm().Match(tfm);
            return m.Success
                ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))
                : (0, 0);
        }
        return (TfmSdkMajor(tfm), 0);
    }

    private static int TfmSdkMajor(string tfm)
    {
        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            var m = NetCoreAppTfm().Match(tfm);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        return int.Parse(tfm.AsSpan(3).TrimEnd('0').TrimEnd('.'));
    }

    private static IEnumerable<string> CollectCoreAppTfms(IEnumerable<string> paths) =>
        paths.SelectMany(p => ExtractCoreAppTfms(SafeRead(p)));

    private static IEnumerable<string> ImportedBuildTfms(string srcDir, string csprojPath)
    {
        if (!SafeRead(csprojPath).Contains('$', StringComparison.Ordinal)) yield break;

        foreach (var props in new[]
                 {
                     Path.Combine(srcDir, "eng", "Versions.props"),
                     Path.Combine(srcDir, "build", "TargetFrameworks.props"),
                     Path.Combine(srcDir, "Directory.Build.props"),
                 }.Where(File.Exists))
        {
            foreach (Match m in BuildPropertyTfmRe().Matches(SafeRead(props)))
            foreach (var tfm in ExtractCoreAppTfms(m.Groups[1].Value))
                yield return tfm;

            if (ReadDefaultNetCoreTargetFramework(props) is { } defaultTfm)
                yield return defaultTfm;
        }
    }

    [GeneratedRegex(@"<(?:\w+TargetFramework)>\s*([^<]+?)\s*</", RegexOptions.IgnoreCase)]
    private static partial Regex BuildPropertyTfmRe();

    [GeneratedRegex(@"<DefaultNetCoreTargetFramework>\s*([^<]+?)\s*</DefaultNetCoreTargetFramework>",
        RegexOptions.IgnoreCase)]
    private static partial Regex DefaultNetCoreTfmRe();

    private static string? ResolveDefaultNetCoreTargetFramework(string srcDir, string? csprojPath = null)
    {
        if (csprojPath is not null)
        {
            for (var dir = Path.GetDirectoryName(csprojPath);
                 dir is not null && dir.StartsWith(srcDir, StringComparison.Ordinal);
                 dir = Path.GetDirectoryName(dir))
            {
                if (ReadDefaultNetCoreTargetFramework(Path.Combine(dir, "Directory.Build.props")) is { } fromDir)
                    return fromDir;
            }
        }

        return ReadDefaultNetCoreTargetFramework(Path.Combine(srcDir, "test", "Directory.Build.props"))
            ?? ReadDefaultNetCoreTargetFramework(Path.Combine(srcDir, "eng", "Versions.props"))
            ?? ReadDefaultNetCoreTargetFramework(Path.Combine(srcDir, "build", "TargetFrameworks.props"))
            ?? ReadDefaultNetCoreTargetFramework(Path.Combine(srcDir, "Directory.Build.props"));
    }

    private static string? ReadDefaultNetCoreTargetFramework(string propsPath)
    {
        if (!File.Exists(propsPath)) return null;
        var m = DefaultNetCoreTfmRe().Match(SafeRead(propsPath));
        if (!m.Success) return null;
        var raw = m.Groups[1].Value.Trim();
        return ExtractCoreAppTfms(raw).FirstOrDefault() ?? raw;
    }

    private static IEnumerable<string> ProjectTfms(string srcDir, string csprojPath)
    {
        foreach (var tfm in ProjectTfms(csprojPath)) yield return tfm;

        var text = SafeRead(csprojPath);
        if (!text.Contains("$(DefaultNetCoreTargetFramework)", StringComparison.Ordinal)) yield break;
        if (ResolveDefaultNetCoreTargetFramework(srcDir, csprojPath) is { } defaultTfm)
            yield return defaultTfm;
    }

    private static IEnumerable<string> ProjectTfms(string csprojPath)
    {
        var text = SafeRead(csprojPath);
        // Prefer <Otherwise> TFMs when projects use Choose/When (e.g. ImageSharp preview vs release).
        var scan = OtherwiseBlockRe().Match(text) is { Success: true } o ? o.Groups[1].Value : text;
        foreach (Match m in TargetFrameworkRe().Matches(scan))
        {
            foreach (var part in m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var tfm in ExtractCoreAppTfms(part))
                yield return tfm;
        }
    }

    [GeneratedRegex(@"<Otherwise>\s*(.*?)\s*</Otherwise>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OtherwiseBlockRe();

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<]+?)\s*</TargetFrameworks?>", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkRe();

    private static IEnumerable<string> ExtractCoreAppTfms(string text)
    {
        foreach (Match m in NetCoreAppTfm().Matches(text))
            yield return $"netcoreapp{m.Groups[1].Value}.{m.Groups[2].Value}";

        foreach (Match m in NetCoreTfm().Matches(text))
        {
            var major = m.Groups[1].Value;
            yield return m.Groups[2].Success ? $"net{major}.0" : $"net{major}";
        }
    }

    private static IEnumerable<string> TestTfmSources(string srcDir)
    {
        foreach (var f in TestProjectFiles(srcDir)) yield return f;
        foreach (var dir in new[] { "test", "tests" }.Select(d => Path.Combine(srcDir, d)).Where(Directory.Exists))
        foreach (var f in Directory.EnumerateFiles(dir, "Directory.Build.props", SearchOption.AllDirectories))
            yield return f;
    }

    private static int? ReadGlobalJsonMajor(string srcDir)
    {
        var gj = Directory.EnumerateFiles(srcDir, "global.json", SearchOption.AllDirectories)
            .OrderBy(p => p.Length) // shallowest first
            .FirstOrDefault();
        if (gj is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(SafeRead(gj));
            if (doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                sdk.TryGetProperty("version", out var ver) &&
                ver.GetString() is { } v &&
                int.TryParse(v.Split('.')[0], out var major))
                return major;
        }
        catch { /* malformed global.json — ignore */ }
        return null;
    }

    private static IEnumerable<string> TestProjectFiles(string srcDir) =>
        Directory.EnumerateFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(f =>
            {
                var text = SafeRead(f);
                return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("NUnit", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("MSTest", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(f).Contains("Test", StringComparison.OrdinalIgnoreCase);
            });

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }
}
