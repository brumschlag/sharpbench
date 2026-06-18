using System.Diagnostics;
using System.Text;

namespace SharpBench;

/// <summary>
/// Agentic solver backed by the Pi coding agent (https://pi.dev). Unlike the oracle-file solver,
/// Pi gets only the issue text and explores the checked-out repo itself (read/grep/edit tools),
/// then edits files in place. The evaluator captures the resulting diff and runs the gating tests.
///
/// Runs headless: `pi --model <provider>/<id> -p "<issue>"` in the repo dir, or
/// `pi --provider <provider> --model <id>` when the model id has no provider prefix.
/// (Anthropic adaptive-thinking models get `--thinking off` to avoid a 400 on opus-4-8.)
/// </summary>
public sealed class PiSolver : ISolver
{
    private readonly string _bin;
    private readonly string? _provider;
    private readonly string _model;
    private readonly bool _thinkingOff;
    private readonly string _logDir;
    private readonly int _timeoutSeconds;

    public string Name => _provider is null ? $"pi({_model})" : $"pi({_provider}/{_model})";
    public bool SupportsIteration => true;

    public PiSolver(string logDir, string model = "anthropic/claude-opus-4-8",
        string? provider = null, string? bin = null, int timeoutSeconds = 900)
    {
        _logDir = logDir;
        (_provider, _model) = ResolveProviderModel(model, provider);
        _thinkingOff = IsAnthropicProvider(_provider, model);
        _bin = bin ?? ResolveBin();
        _timeoutSeconds = timeoutSeconds;
    }

    /// <summary>
    /// Maps CLI <c>--model</c> / optional <c>--provider</c> to pi flags.
    /// <c>openai/gpt-4o</c> → <c>--model openai/gpt-4o</c>; bare <c>gpt-4o</c> + <c>--provider openai</c>
    /// → <c>--provider openai --model gpt-4o</c>.
    /// </summary>
    internal static (string? Provider, string Model) ResolveProviderModel(string model, string? providerOverride)
    {
        var slash = model.IndexOf('/');
        if (slash > 0)
        {
            var prefixedProvider = model[..slash];
            var bareModel = model[(slash + 1)..];
            if (providerOverride is not null)
                return (providerOverride, bareModel);
            // Pi accepts provider/model in --model without a separate --provider flag.
            return (null, model);
        }

        return (providerOverride ?? "anthropic", model);
    }

    private static bool IsAnthropicProvider(string? provider, string rawModel)
    {
        if (provider is not null)
            return provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase);
        var slash = rawModel.IndexOf('/');
        return slash > 0 && rawModel[..slash].Equals("anthropic", StringComparison.OrdinalIgnoreCase);
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
        foreach (var a in BuildPiArgs(prompt)) psi.ArgumentList.Add(a);

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

    private IEnumerable<string> BuildPiArgs(string prompt)
    {
        if (_provider is not null)
        {
            yield return "--provider";
            yield return _provider;
        }

        yield return "--model";
        yield return _model;

        if (_thinkingOff)
        {
            yield return "--thinking";
            yield return "off";
        }

        yield return "--no-session";
        yield return "-nc";
        yield return "-p";
        yield return prompt;
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
