namespace SharpBench;

/// <summary>
/// The reference "solver": applies the dataset's gold patch. Used to validate the harness —
/// every gold patch should resolve. Not a real solver; it's the upper bound / smoke test.
/// </summary>
public sealed class GoldSolver : ISolver
{
    public string Name => "gold";

    public async Task SolveAsync(BenchCase c, string srcDir, string? feedback, CancellationToken ct = default)
    {
        var patchPath = Path.Combine(srcDir, "..", "gold.patch");
        await File.WriteAllTextAsync(patchPath, c.Patch, ct);

        var (code, output) = await GitPatchApplicator.ApplyAsync(srcDir, patchPath, ct, timeoutSeconds: 1800);
        if (code != 0)
            throw new InvalidOperationException($"gold patch failed to apply: {output}");
    }
}
