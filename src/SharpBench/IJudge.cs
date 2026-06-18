namespace SharpBench;

/// <summary>Judges a candidate patch against a benchmark case.</summary>
public interface IJudge
{
    /// <param name="benchCase">The task (problem statement, gold patch, tests).</param>
    /// <param name="candidatePatch">The diff to evaluate. For a smoke test, pass the gold patch.</param>
    Task<Verdict> JudgeAsync(BenchCase benchCase, string candidatePatch, CancellationToken ct = default);
}
