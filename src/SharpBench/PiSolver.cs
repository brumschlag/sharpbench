using System.Diagnostics;
using System.Text;

namespace SharpBench;

/// <summary>
/// Agentic solver backed by the Pi coding agent (https://pi.dev). Unlike the oracle-file solver,
/// Pi gets only the issue text and explores the checked-out repo itself (read/grep/edit tools),
/// then edits files in place. The evaluator captures the resulting diff and runs the gating tests.
///
/// Runs headless: `pi --provider anthropic --model ... --thinking off -p "<issue>"` in the repo dir.
/// (Pi 0.74.x predates adaptive-thinking models, so we disable thinking to avoid a 400 on opus-4-8.)
/// </summary>
public sealed class PiSolver : ISolver
{
    private readonly string _bin;
    private readonly string _model;
    private readonly string _logDir;
    private readonly int _timeoutSeconds;

    public string Name => $"pi({_model})";
    public bool SupportsIteration => true;

    public PiSolver(string logDir, string model = "anthropic/claude-opus-4-8",
        string? bin = null, int timeoutSeconds = 900)
    {
        _logDir = logDir;
        _model = model;
        _bin = bin ?? ResolveBin();
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task SolveAsync(BenchCase c, string srcDir, string? feedback, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(c, feedback);

        var psi = new ProcessStartInfo(_bin)
        {
            WorkingDirectory = srcDir,                 // Pi operates on its cwd
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
        {
            "--provider", "anthropic",
            "--model", _model,
            "--thinking", "off",
            "--no-session",
            "-nc",                                     // ignore AGENTS.md/CLAUDE.md in the target repo
            "-p", prompt,
        }) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        try { await proc.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            await DumpAsync(c, output, ct);
            throw new TimeoutException($"pi exceeded {_timeoutSeconds}s");
        }

        await DumpAsync(c, output, ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"pi exited {proc.ExitCode}; see {c.InstanceId}.pi.log");
    }

    private async Task DumpAsync(BenchCase c, StringBuilder output, CancellationToken ct)
    {
        try { await File.WriteAllTextAsync(Path.Combine(_logDir, $"{c.InstanceId}.pi.log"), output.ToString(), ct); }
        catch { /* best effort */ }
    }

    private static string BuildPrompt(BenchCase c, string? feedback)
    {
        var sb = new StringBuilder();
        if (feedback is null)
        {
            sb.AppendLine("You are fixing a bug in this C#/.NET repository. Explore the code as needed and edit");
            sb.AppendLine("the source files to resolve the issue below. Make the minimal change that fixes it.");
            sb.AppendLine("You MAY read existing tests and call sites to understand the expected API/behavior,");
            sb.AppendLine("but do NOT modify, add, or delete any test files. Ensure your change keeps the whole");
            sb.AppendLine("solution compiling — if existing callers use a member, don't reduce its accessibility.");
            sb.AppendLine();
            sb.AppendLine("## Issue");
            sb.AppendLine(c.ProblemStatement);
            if (!string.IsNullOrWhiteSpace(c.HintsText) && c.HintsText != "nan")
            {
                sb.AppendLine();
                sb.AppendLine("## Hints");
                sb.AppendLine(c.HintsText);
            }
        }
        else
        {
            // Retry: the working tree still has your previous edits. Fix the reported failures.
            sb.AppendLine("Your previous attempt at the fix below did not pass verification. The repository still");
            sb.AppendLine("contains your edits. Build/test feedback follows — adjust the SOURCE to make it build");
            sb.AppendLine("cleanly and keep the existing tests green. Do NOT modify test files.");
            sb.AppendLine();
            sb.AppendLine("## Original issue");
            sb.AppendLine(c.ProblemStatement);
            sb.AppendLine();
            sb.AppendLine("## Verification feedback (build errors and/or failing existing tests)");
            sb.AppendLine(feedback);
        }
        return sb.ToString();
    }

    /// <summary>Locate the pi binary: PI_BIN env, else npm global prefix, else PATH ("pi").</summary>
    private static string ResolveBin()
    {
        var env = Environment.GetEnvironmentVariable("PI_BIN");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        try
        {
            var psi = new ProcessStartInfo("npm", "config get prefix")
            { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var prefix = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var candidate = Path.Combine(prefix, "bin", "pi");
            if (File.Exists(candidate)) return candidate;
        }
        catch { /* fall through */ }

        return "pi"; // rely on PATH
    }
}
