using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpBench;

/// <summary>Outcome of a single named test after the patch was applied and the suite ran.</summary>
public sealed class TestOutcome
{
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    /// <summary>True if the test was expected but no matching result was found in the TRX.</summary>
    public bool Missing { get; set; }
}

/// <summary>Ground-truth (execution-based) evaluation of one case, SWE-bench style.</summary>
public sealed class EvaluationResult
{
    public string InstanceId { get; set; } = "";
    public string Repo { get; set; } = "";

    /// <summary>Which solver produced the candidate (e.g. "gold", "claude(oracle)").</summary>
    public string Solver { get; set; } = "";

    /// <summary>Number of solver attempts used (1 for one-shot solvers; up to --max-rounds for iterative).</summary>
    public int Rounds { get; set; } = 1;

    /// <summary>The candidate diff the solver produced (git diff of its edits). May be large.</summary>
    [JsonIgnore] public string GeneratedPatch { get; set; } = "";

    /// <summary>Detected (or overridden) SDK image and target framework the tests ran under.</summary>
    public string SdkImage { get; set; } = "";
    public string Framework { get; set; } = "";

    /// <summary>The headline metric: all FAIL_TO_PASS pass AND all PASS_TO_PASS pass.</summary>
    public bool Resolved { get; set; }

    public bool Built { get; set; }
    public List<TestOutcome> FailToPass { get; set; } = new();
    public List<TestOutcome> PassToPass { get; set; } = new();

    public double DurationSeconds { get; set; }
    public string? Error { get; set; }

    public bool AllFailToPassPassed => FailToPass.Count > 0 && FailToPass.All(t => t.Passed);
    public bool AllPassToPassPassed => PassToPass.All(t => t.Passed); // vacuously true if empty
}

/// <summary>Parses the dataset's Python-list-literal test columns, e.g. <c>['A.B.C', 'D.E.F']</c>.</summary>
public static partial class TestListParser
{
    [GeneratedRegex(@"'([^']+)'|""([^""]+)""")]
    private static partial Regex QuotedItem();

    public static List<string> Parse(string? listLiteral)
    {
        if (string.IsNullOrWhiteSpace(listLiteral)) return new();
        return QuotedItem().Matches(listLiteral)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(s => s.Length > 0)
            .ToList();
    }
}
