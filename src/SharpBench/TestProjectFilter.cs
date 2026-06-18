using System.Text.RegularExpressions;

namespace SharpBench;

/// <summary>Test .csproj paths unsuitable for Linux Docker grading (GUI, Windows, mobile, apps).</summary>
internal static partial class TestProjectFilter
{
    [GeneratedRegex(
        @"/(benchmarks?|samples?|interactive)/|InteractiveTests|Benchmarks|Appium|Direct2D1|RenderTests|WpfCompare|DesignerSupport\.TestApp|\.TestApp\.csproj|IntegrationTests",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedProjectRe();

    /// <summary>grep -Ev pattern for bash project discovery.</summary>
    public const string GrepExcludePattern =
        "/(benchmarks?|samples?|interactive)/|InteractiveTests|Benchmarks|Appium|Direct2D1|RenderTests|WpfCompare|DesignerSupport\\.TestApp|\\.TestApp\\.csproj|IntegrationTests";

    public static bool IsExcluded(string repoRelativeProjectPath) =>
        ExcludedProjectRe().IsMatch(repoRelativeProjectPath.Replace('\\', '/'));
}
