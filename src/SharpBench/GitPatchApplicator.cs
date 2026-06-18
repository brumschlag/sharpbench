using System.Diagnostics;

namespace SharpBench;

/// <summary>Apply unified diffs with a whitespace-tolerant fallback (CRLF vs LF repos).</summary>
internal static class GitPatchApplicator
{
    public static async Task<(int ExitCode, string Output)> ApplyAsync(
        string srcDir, string patchPath, CancellationToken ct, int timeoutSeconds)
    {
        var (code, output) = await RunAsync(srcDir, patchPath, ct, timeoutSeconds);
        if (code == 0) return (code, output);

        var (retryCode, retryOut) = await RunAsync(srcDir, patchPath, ct, timeoutSeconds, "--ignore-whitespace");
        return retryCode == 0
            ? (retryCode, output + "\n[retried with --ignore-whitespace]\n" + retryOut)
            : (code, output);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string srcDir, string patchPath, CancellationToken ct, int timeoutSeconds, params string[] extra)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(srcDir);
        psi.ArgumentList.Add("apply");
        psi.ArgumentList.Add("--verbose");
        foreach (var arg in extra) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(patchPath);

        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, string.IsNullOrEmpty(stderr) ? stdout : stderr);
    }
}
