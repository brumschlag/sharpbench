using System.Text.Json;
using SharpBench;

// ── Args ──────────────────────────────────────────────────────────────────
// Usage: dotnet run -- [--mode judge|execute] [--data PATH] [--limit N]
//                      [--repo SUBSTR] [--instance ID] [--sdk-image IMG] [--out DIR]
//                      [--solver gold|claude|pi] [--model ID] [--provider ID]
string mode = ArgValue("--mode") ?? "judge";
string dataPath = ArgValue("--data") ?? "data/swe-sharp-bench.csv";
int limit = int.TryParse(ArgValue("--limit"), out var l) ? l : 3;
string? repoFilter = ArgValue("--repo");
string? instance = ArgValue("--instance");
string outDir = ArgValue("--out") ?? "results";

if (!File.Exists(dataPath))
{
    Console.Error.WriteLine($"Dataset not found: {dataPath}");
    Console.Error.WriteLine("Run from the repo root, or pass --data <path to swe-sharp-bench.csv>.");
    return 1;
}

var allCases = DatasetLoader.Load(dataPath);
Directory.CreateDirectory(outDir);
var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

// ── Execute mode: ground-truth, SWE-bench-style test execution in Docker ────
if (mode.Equals("execute", StringComparison.OrdinalIgnoreCase))
    return await RunExecuteMode();

// ── Judge mode (default): LLM-as-judge ──────────────────────────────────────
var cases = allCases
    .Where(c => repoFilter is null || c.Repo.Contains(repoFilter, StringComparison.OrdinalIgnoreCase))
    .Take(limit)
    .ToList();

Console.WriteLine($"Loaded {allCases.Count} cases; judging {cases.Count}" +
                  (repoFilter is null ? "" : $" (repo ~ \"{repoFilter}\")") + ".");

if (cases.Count == 0) { Console.WriteLine("Nothing to judge."); return 0; }

var judge = new ClaudeJudge();

// ── Judge (bounded concurrency) ─────────────────────────────────────────────
// NOTE: candidate == gold patch here, so this is a smoke test: a well-behaved judge
// should return "correct" for almost all of these. Swap in a solver's patch to evaluate it.
var gate = new SemaphoreSlim(4);
var results = new List<JudgedCase>();

await Parallel.ForEachAsync(cases, async (c, ct) =>
{
    await gate.WaitAsync(ct);
    try
    {
        var verdict = await judge.JudgeAsync(c, candidatePatch: c.Patch, ct);
        var judged = new JudgedCase { InstanceId = c.InstanceId, Repo = c.Repo, Verdict = verdict };

        lock (results) results.Add(judged);
        await File.WriteAllTextAsync(
            Path.Combine(outDir, $"{c.InstanceId}.json"),
            JsonSerializer.Serialize(judged, jsonOpts), ct);

        Console.WriteLine($"  [{verdict.Result,-9}] {c.InstanceId}  (conf {verdict.Confidence:0.00})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  [ERROR    ] {c.InstanceId}: {ex.Message}");
    }
    finally { gate.Release(); }
});

// ── Summary ─────────────────────────────────────────────────────────────────
await File.WriteAllTextAsync(
    Path.Combine(outDir, "_summary.json"),
    JsonSerializer.Serialize(results.OrderBy(r => r.InstanceId), jsonOpts));

var correct = results.Count(r => r.Verdict.Result == "correct");
Console.WriteLine($"\nDone. {correct}/{results.Count} judged \"correct\". Results in {outDir}/");
return 0;

// ── Execute mode implementation ─────────────────────────────────────────────
async Task<int> RunExecuteMode()
{
    // Select by --instance (one case), --repo (a batch), or --all (the whole dataset).
    bool all = args.Contains("--all");
    List<BenchCase> selected;
    if (instance is not null)
    {
        selected = allCases.Where(c => c.InstanceId == instance).ToList();
    }
    else if (repoFilter is not null)
    {
        var matched = allCases
            .Where(c => c.Repo.Contains(repoFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.InstanceId);
        selected = (ArgValue("--limit") is not null ? matched.Take(limit) : matched).ToList();
    }
    else if (all)
    {
        // Smallest repos first so we bank easy results fast; heavy repos (Avalonia: 41) run last.
        var sizeByRepo = allCases.GroupBy(c => c.Repo).ToDictionary(g => g.Key, g => g.Count());
        selected = allCases
            .OrderBy(c => sizeByRepo[c.Repo]).ThenBy(c => c.Repo).ThenBy(c => c.InstanceId)
            .ToList();
    }
    else
    {
        Console.Error.WriteLine("execute mode needs --instance <id>, --repo <substr>, or --all.");
        return 1;
    }

    // --skip-done: resume a long run by skipping cases that already have a result on disk.
    if (args.Contains("--skip-done"))
        selected = selected.Where(c => !File.Exists(Path.Combine(outDir, $"{c.InstanceId}.exec.json"))).ToList();

    if (selected.Count == 0)
    {
        Console.Error.WriteLine("No cases to run (all matched cases already done?).");
        return 0;
    }

    // By default the SDK image + framework are auto-detected per case (global.json + test .csproj).
    // Override either with --sdk-image / --framework when detection guesses wrong.
    var sdkImage = ArgValue("--sdk-image");
    var framework = ArgValue("--framework");
    var auto = sdkImage is null && framework is null ? " (auto-detect)" : "";

    // --solver gold (apply gold patch) | claude (oracle-file rewrite) | pi (agentic, explores repo itself)
    ISolver solver = (ArgValue("--solver") ?? "gold").ToLowerInvariant() switch
    {
        "claude" => new ClaudeSolver(),
        "pi" => new PiSolver(outDir, ArgValue("--model") ?? "anthropic/claude-opus-4-8",
            provider: ArgValue("--provider")),
        "gold" => new GoldSolver(),
        var s => throw new ArgumentException($"unknown --solver '{s}' (use gold|claude|pi)"),
    };
    // Iterative solvers (pi) verify with build + PASS_TO_PASS between attempts, up to --max-rounds (default 3).
    int maxRounds = int.TryParse(ArgValue("--max-rounds"), out var mr) ? Math.Max(1, mr) : 3;
    var roundsNote = solver.SupportsIteration && maxRounds > 1 ? $", max-rounds={maxRounds}" : "";
    Console.WriteLine($"Execute mode: {selected.Count} case(s){auto}, solver={solver.Name}{roundsNote} (Docker).");

    // Per-process timeout (seconds) — bounds a single hung build so one bad case can't stall a 150-case run.
    int timeoutSec = int.TryParse(ArgValue("--timeout"), out var ts) ? ts : 900;
    var evaluator = new ExecutionEvaluator(outDir, sdkImage, framework, maxRounds, timeoutSec);
    var execResults = new List<EvaluationResult>();

    foreach (var c in selected) // serial: each run pulls images / restores NuGet
    {
        Console.WriteLine($"\n▶ {c.InstanceId} ({c.Repo} @ {c.BaseCommit[..10]})");
        var r = await evaluator.EvaluateAsync(c, solver);
        execResults.Add(r);

        await File.WriteAllTextAsync(Path.Combine(outDir, $"{c.InstanceId}.exec.json"),
            JsonSerializer.Serialize(r, jsonOpts));

        var f2p = $"{r.FailToPass.Count(t => t.Passed)}/{r.FailToPass.Count}";
        var p2p = $"{r.PassToPass.Count(t => t.Passed)}/{r.PassToPass.Count}";
        var roundsTxt = r.Rounds > 1 ? $"  rounds={r.Rounds}" : "";
        Console.WriteLine($"  {r.SdkImage} · {r.Framework}");
        Console.WriteLine($"  resolved={r.Resolved}  built={r.Built}  F2P {f2p}  P2P {p2p}{roundsTxt}  ({r.DurationSeconds}s)");
        if (r.Error is not null) Console.WriteLine($"  error: {r.Error}");
        if (!r.Resolved) // only dump per-test detail when something went wrong
            foreach (var t in r.FailToPass.Concat(r.PassToPass).Where(t => !t.Passed))
                Console.WriteLine($"    [{(t.Missing ? "MISSING" : "FAIL")}] {t.Name}");
    }

    // ── Batch summary ───────────────────────────────────────────────────────
    await File.WriteAllTextAsync(Path.Combine(outDir, "_exec_summary.json"),
        JsonSerializer.Serialize(execResults.OrderBy(r => r.InstanceId), jsonOpts));

    var resolved = execResults.Count(r => r.Resolved);
    Console.WriteLine($"\n── Summary ──");
    foreach (var r in execResults.OrderBy(r => r.InstanceId))
        Console.WriteLine($"  {(r.Resolved ? "✓" : "✗")} {r.InstanceId,-40} {r.Framework,-8} {r.DurationSeconds,6}s" +
                          (r.Resolved ? "" : $"  ({r.Error ?? "tests failed"})"));
    Console.WriteLine($"\nResolved {resolved}/{execResults.Count}. Results + _exec_summary.json in {outDir}/");
    return resolved == execResults.Count ? 0 : 2;
}

string? ArgValue(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
