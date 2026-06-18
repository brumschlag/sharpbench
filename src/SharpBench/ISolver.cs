namespace SharpBench;

/// <summary>
/// Produces a candidate fix for a case by editing the checked-out repo working tree in place.
/// The evaluator captures `git diff` afterwards as the candidate patch, then runs the gating tests.
/// </summary>
public interface ISolver
{
    string Name { get; }

    /// <summary>Whether the evaluator should run the verify→retry loop for this solver
    /// (build + PASS_TO_PASS feedback). One-shot solvers (gold, oracle rewrite) return false.</summary>
    bool SupportsIteration => false;

    /// <param name="benchCase">The task.</param>
    /// <param name="srcDir">The repo working tree (carries edits across iterations). Edit source files here.</param>
    /// <param name="feedback">Null on the first attempt; on a retry, the build/PASS_TO_PASS failures from the
    /// previous attempt (the held-out FAIL_TO_PASS results are never included). Iterative solvers use it.</param>
    Task SolveAsync(BenchCase benchCase, string srcDir, string? feedback, CancellationToken ct = default);
}
