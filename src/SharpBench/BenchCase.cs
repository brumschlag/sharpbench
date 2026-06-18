using CsvHelper.Configuration.Attributes;

namespace SharpBench;

/// <summary>
/// One SWE-Sharp-Bench task. Mirrors the columns of swe-sharp-bench.csv
/// (SWE-bench format: a real GitHub issue + the gold fix + the tests that gate it).
/// </summary>
public sealed class BenchCase
{
    [Name("repo")] public string Repo { get; set; } = "";
    [Name("instance_id")] public string InstanceId { get; set; } = "";
    [Name("base_commit")] public string BaseCommit { get; set; } = "";

    /// <summary>The gold (reference) fix, as a unified git diff.</summary>
    [Name("patch")] public string Patch { get; set; } = "";

    /// <summary>The diff that introduces/updates the gating tests.</summary>
    [Name("test_patch")] public string TestPatch { get; set; } = "";

    /// <summary>The issue / bug report text the solver is given.</summary>
    [Name("problem_statement")] public string ProblemStatement { get; set; } = "";

    [Name("hints_text")] public string? HintsText { get; set; }
    [Name("created_at")] public string? CreatedAt { get; set; }
    [Name("version")] public string? Version { get; set; }

    /// <summary>Tests expected to flip from failing to passing once the fix is applied (JSON-ish list string).</summary>
    [Name("FAIL_TO_PASS")] public string FailToPass { get; set; } = "";

    /// <summary>Tests expected to stay passing (guards against regressions).</summary>
    [Name("PASS_TO_PASS")] public string PassToPass { get; set; } = "";
}
