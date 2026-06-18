using System.Text.RegularExpressions;

namespace SharpBench;

/// <summary>
/// Some dataset rows ship empty FAIL_TO_PASS / PASS_TO_PASS lists. Infer gating tests from the test patch.
/// </summary>
internal static partial class TestPatchInferrer
{
    [GeneratedRegex(@"^\+\+\+ b/(.+)$", RegexOptions.Multiline)]
    private static partial Regex PatchPathRe();

    [GeneratedRegex(@"public (?:async )?(?:Task(?:<[^>]+>)?|void) (\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex PublicMethodRe();

    [GeneratedRegex(@"namespace\s+([\w.]+)", RegexOptions.Multiline)]
    private static partial Regex NamespaceRe();

    [GeneratedRegex(@"\[(?:Fact|Theory|ConditionalFact|ConditionalTheory)(?:[^\]]*)\]\s*(?:public )?(?:async )?(?:Task(?:<[^>]+>)?|void) (\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex TestMethodRe();

    /// <summary>Returns fully-qualified test names to treat as PASS_TO_PASS when the dataset lists are empty.</summary>
    public static List<string> InferPassToPass(string srcDir, string testPatch)
    {
        var paths = PatchPathRe().Matches(testPatch)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(p => p.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        paths = PreferInferencePaths(paths);

        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in paths)
        {
            var full = Path.Combine(srcDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            var text = File.ReadAllText(full);
            var ns = NamespaceRe().Match(text).Groups[1].Value;
            var cls = Path.GetFileNameWithoutExtension(rel);
            if (ns.Length == 0 || cls.Length == 0) continue;

            var filePatch = ExtractFilePatch(testPatch, rel);
            var fromPatch = PublicMethodRe().Matches(filePatch)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            var methods = fromPatch.Count > 0
                ? fromPatch
                : MethodsTouchingPatch(text, testPatch, rel).ToHashSet(StringComparer.Ordinal);

            if (methods.Count == 0)
            {
                foreach (Match m in TestMethodRe().Matches(text))
                    methods.Add(m.Groups[1].Value);
            }

            foreach (var method in methods)
                results.Add($"{ns}.{cls}.{method}");
        }

        return results.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Dataset patches often mirror design/unit tests into provider functional suites. Grade the
    /// focused unit tests when both are present (e.g. EF Core <c>Design.Tests</c>).
    /// </summary>
    private static List<string> PreferInferencePaths(List<string> paths)
    {
        var design = paths.Where(p => p.Contains("Design.Tests", StringComparison.OrdinalIgnoreCase)).ToList();
        if (design.Count > 0) return design;

        var unit = paths.Where(p => !p.Contains("FunctionalTests", StringComparison.OrdinalIgnoreCase)).ToList();
        return unit.Count > 0 ? unit : paths;
    }

    private static string ExtractFilePatch(string patch, string relPath)
    {
        var sb = new System.Text.StringBuilder();
        var inFile = false;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inFile = line.Contains($" b/{relPath}", StringComparison.Ordinal);
                continue;
            }
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                inFile = line.EndsWith(relPath, StringComparison.Ordinal);
                continue;
            }
            if (inFile) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static IEnumerable<string> MethodsTouchingPatch(string fileText, string patch, string relPath)
    {
        var needles = SignificantRemovedLines(patch, relPath).ToList();
        if (needles.Count == 0) yield break;

        var methods = TestMethodRe().Matches(fileText)
            .Select(m => (Name: m.Groups[1].Value, Index: m.Index))
            .OrderBy(m => m.Index)
            .ToList();

        for (var i = 0; i < methods.Count; i++)
        {
            var start = methods[i].Index;
            var end = i + 1 < methods.Count ? methods[i + 1].Index : fileText.Length;
            var body = fileText[start..end];
            if (needles.Any(n => body.Contains(n, StringComparison.Ordinal)))
                yield return methods[i].Name;
        }
    }

    private static IEnumerable<string> SignificantRemovedLines(string patch, string relPath)
    {
        var inFile = false;
        foreach (var line in patch.Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                inFile = line.EndsWith(relPath, StringComparison.Ordinal);
                continue;
            }
            if (!inFile || !line.StartsWith('-') || line.StartsWith("---", StringComparison.Ordinal)) continue;
            var content = line[1..].Trim();
            if (content.Length >= 20 && !content.StartsWith("using ", StringComparison.Ordinal))
                yield return content;
        }
    }
}
