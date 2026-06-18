using System.Diagnostics;

namespace SharpBench;

/// <summary>
/// Ground-truth, SWE-bench-style evaluator.
///
/// Host side: shallow-fetch repo@base_commit, git-apply the candidate + test patches, then probe
/// global.json / test .csproj to pick the SDK image + target framework.
/// Container side: run only the named gating tests in the matching .NET SDK image.
/// resolved = all FAIL_TO_PASS pass AND all PASS_TO_PASS pass.
///
/// Pass <paramref name="sdkImageOverride"/>/<paramref name="frameworkOverride"/> to skip detection.
/// </summary>
public sealed class ExecutionEvaluator
{
    private readonly string _logDir;
    private readonly string? _sdkImageOverride;
    private readonly string? _frameworkOverride;
    private readonly int _maxRounds;
    private readonly int _timeoutSeconds;

    /// <param name="maxRounds">Max solver attempts for iterative solvers (build + PASS_TO_PASS verify between
    /// attempts). One-shot solvers always run a single attempt regardless.</param>
    public ExecutionEvaluator(string logDir, string? sdkImageOverride = null,
        string? frameworkOverride = null, int maxRounds = 1, int timeoutSeconds = 1800)
    {
        _logDir = logDir;
        _sdkImageOverride = sdkImageOverride;
        _frameworkOverride = frameworkOverride;
        _maxRounds = Math.Max(1, maxRounds);
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<EvaluationResult> EvaluateAsync(BenchCase c, ISolver solver, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new EvaluationResult { InstanceId = c.InstanceId, Repo = c.Repo, Solver = solver.Name };

        var f2p = TestListParser.Parse(c.FailToPass);
        var p2p = TestListParser.Parse(c.PassToPass);

        // Unique scratch dir (never reused → no need to delete container-written, root-owned bin/obj).
        var work = Path.Combine(Path.GetTempPath(), "sharpbench", $"{c.InstanceId}-{Guid.NewGuid():N}");
        var src = Path.Combine(work, "src");
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(outDir);
        var setupLog = new System.Text.StringBuilder();

        try
        {
            // ── Host: fetch repo@commit ─────────────────────────────────────────────
            if (!await GitFetchAsync(src, c, setupLog, ct))
            {
                result.Error = "git fetch/checkout failed; see exec.log";
                await File.WriteAllTextAsync(Path.Combine(_logDir, $"{c.InstanceId}.exec.log"), setupLog.ToString(), ct);
                return result;
            }

            GlobalJsonPatcher.PatchRollForward(src);

            if (f2p.Count == 0 && p2p.Count == 0)
            {
                p2p = TestPatchInferrer.InferPassToPass(src, c.TestPatch);
                if (p2p.Count == 0)
                {
                    result.Error = "No FAIL_TO_PASS or PASS_TO_PASS tests; cannot evaluate.";
                    return result;
                }
                setupLog.AppendLine($"[inferred {p2p.Count} PASS_TO_PASS test(s) from test patch]");
            }

            var allTests = f2p.Concat(p2p).Distinct().ToList();
            if (OperatingSystem.IsLinux() && allTests.Count > 0 && allTests.All(WindowsOnlyTest.IsWindowsOnly))
            {
                result.Error = "Windows-only tests (COM/interop); cannot evaluate on Linux.";
                return result;
            }

            // ── Detect SDK image + framework up front (needed for the verify loop) ──
            var plan = RepoProbe.Detect(src);
            var sdkImage = _sdkImageOverride ?? plan.SdkImage;
            var framework = _frameworkOverride ?? plan.Framework;
            result.SdkImage = sdkImage;
            result.Framework = framework;

            // ── Solve → (build + PASS_TO_PASS verify) → retry loop. Held-out FAIL_TO_PASS never used here. ──
            int rounds = solver.SupportsIteration ? _maxRounds : 1;
            string? feedback = null;
            for (int round = 1; round <= rounds; round++)
            {
                result.Rounds = round;
                try { await solver.SolveAsync(c, src, feedback, ct); }
                catch (Exception ex)
                {
                    result.Error = $"solver '{solver.Name}' failed: {ex.Message}";
                    await File.WriteAllTextAsync(Path.Combine(_logDir, $"{c.InstanceId}.exec.log"),
                        setupLog + "\n[solver error] " + ex, ct);
                    return result;
                }

                // SWE-bench semantics: the dataset owns the test environment. Discard any solver edits to
                // files the test patch touches so the candidate is judged only on its non-test changes.
                foreach (var p in ExtractPaths(c.TestPatch))
                    await RunProcessAsync("git", new[] { "-C", src, "checkout", "--", p }, ct, _timeoutSeconds);

                if (round == rounds) break; // last attempt — go straight to grading

                var verify = await VerifyAsync(work, src, sdkImage, framework, p2p, ct);
                setupLog.AppendLine($"\n===== verify round {round} (built={verify.Built}, failedP2P={verify.FailedP2P.Count}) =====")
                        .AppendLine(verify.Tail);
                if (verify.Built && verify.FailedP2P.Count == 0) break; // builds + no regressions → accept
                feedback = verify.Feedback;
            }

            var (_, diff) = await RunProcessAsync("git", new[] { "-C", src, "diff" }, ct, _timeoutSeconds);
            result.GeneratedPatch = diff;
            await File.WriteAllTextAsync(Path.Combine(_logDir, $"{c.InstanceId}.candidate.patch"), diff, ct);

            // ── Apply the dataset's test patch on top of the solver's (non-test) changes ───────
            if (!string.IsNullOrWhiteSpace(c.TestPatch))
            {
                var testPath = Path.Combine(work, "test.patch");
                await File.WriteAllTextAsync(testPath, c.TestPatch, ct);
                var (applyCode, applyOut) = await GitPatchApplicator.ApplyAsync(src, testPath, ct, _timeoutSeconds);
                setupLog.AppendLine("$ git apply test.patch").AppendLine(applyOut);
                if (applyCode != 0)
                {
                    result.Error = "test patch failed to apply; see exec.log";
                    await File.WriteAllTextAsync(Path.Combine(_logDir, $"{c.InstanceId}.exec.log"), setupLog.ToString(), ct);
                    return result;
                }
            }
            else
                setupLog.AppendLine("[skipped empty test patch]");

            var projectTests = TestProjectResolver.GroupByProject(src, allTests, c.TestPatch);
            var testProjects = projectTests.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
            if (testProjects.Count > 0 && _frameworkOverride is null)
            {
                var targeted = RepoProbe.Detect(src, testProjects);
                framework = targeted.Framework;
                sdkImage = _sdkImageOverride ?? targeted.SdkImage;
                result.Framework = framework;
                result.SdkImage = sdkImage;
            }
            var filter = string.Join("|", allTests.Select(t => $"FullyQualifiedName={t}"));
            var projectFilters = projectTests.ToDictionary(
                kv => kv.Key,
                kv => string.Join("|", kv.Value.Select(t => $"FullyQualifiedName={t}")),
                StringComparer.Ordinal);
            var extraProps = ExtraMsBuildProps(src);
            var skiaDeps = SkiaNativeDeps.Needed(testProjects);
            await File.WriteAllTextAsync(Path.Combine(work, "run.sh"),
                TestRunScript.Test(framework, filter, testProjects, extraProps, skiaDeps, projectFilters), ct);

            // ── Container: dotnet test only (repo already prepared on host) ──────────
            var services = await DockerServiceHost.StartAsync(
                DockerServiceHost.NeedsCosmos(testProjects),
                DockerServiceHost.NeedsSqlServer(testProjects),
                ct, _timeoutSeconds);
            try
            {
                var (exitCode, runLog) = await RunDockerAsync(work, sdkImage, ct, services: services);

                await File.WriteAllTextAsync(Path.Combine(_logDir, $"{c.InstanceId}.exec.log"),
                    setupLog.ToString() + "\n===== container =====\n" + runLog, ct);

                var trxFiles = Directory.GetFiles(outDir, "*.trx");
                var rows = TrxParser.ReadResults(trxFiles);
                result.Built = trxFiles.Length > 0;
                result.FailToPass = f2p.Select(t => TrxParser.Evaluate(t, rows)).ToList();
                result.PassToPass = p2p.Select(t => TrxParser.Evaluate(t, rows)).ToList();
                // Empty FAIL_TO_PASS → vacuously satisfied; rely on PASS_TO_PASS staying green.
                result.Resolved = result.Built
                    && (f2p.Count == 0 || result.AllFailToPassPassed)
                    && result.AllPassToPassPassed;

                if (!result.Built)
                    result.Error = $"No test results produced (build/restore likely failed). docker exit={exitCode}. See {c.InstanceId}.exec.log";
                // A common solver failure: the candidate compiles in isolation but breaks the test project,
                // so the gating tests never run (MISSING). Surface the compiler error instead of bare MISSING.
                else if (!result.Resolved && result.FailToPass.Concat(result.PassToPass).Any(t => t.Missing)
                         && FirstBuildError(runLog) is { } err)
                    result.Error = $"candidate did not compile: {err}";
            }
            finally
            {
                if (services is not null) await services.DisposeAsync();
            }
        }
        finally
        {
            TryCleanup(work);
            sw.Stop();
            result.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        }
        return result;
    }

    // ── Host git: fetch a single commit and check it out ────────────────────────────
    private async Task<bool> GitFetchAsync(string src, BenchCase c, System.Text.StringBuilder log, CancellationToken ct)
    {
        var url = $"https://github.com/{c.Repo}.git";
        var steps = new[]
        {
            new[] { "-C", src, "init", "-q" },
            new[] { "-C", src, "config", "advice.detachedHead", "false" },
            new[] { "-C", src, "remote", "add", "origin", url },
            new[] { "-C", src, "fetch", "-q", "--depth", "1", "origin", c.BaseCommit },
            new[] { "-C", src, "checkout", "-q", "FETCH_HEAD" },
        };
        foreach (var args in steps)
        {
            log.AppendLine($"$ git {string.Join(' ', args)}");
            var (code, output) = await RunProcessAsync("git", args, ct, _timeoutSeconds);
            log.AppendLine(output);
            if (code != 0) { log.AppendLine($"[exit {code}] aborting fetch"); return false; }
        }

        if (NerdbankGitVersioning.IsEnabled(src))
        {
            var deepen = new[] { "-C", src, "fetch", "-q", "--depth", NerdbankGitVersioning.FetchDepth.ToString(), "origin", c.BaseCommit };
            log.AppendLine($"$ git {string.Join(' ', deepen)}");
            var (deepCode, deepOut) = await RunProcessAsync("git", deepen, ct, _timeoutSeconds);
            log.AppendLine(deepOut);
            if (deepCode != 0) { log.AppendLine($"[exit {deepCode}] NBGV deepen fetch failed"); return false; }
        }

        if (File.Exists(Path.Combine(src, ".gitmodules")))
        {
            log.AppendLine("$ git submodule update --init --depth 1 --recursive");
            var (subCode, subOut) = await RunProcessAsync("git",
                new[] { "-C", src, "submodule", "update", "--init", "--depth", "1", "--recursive" },
                ct, _timeoutSeconds);
            log.AppendLine(subOut);
            if (subCode != 0) { log.AppendLine($"[exit {subCode}] submodule init failed"); return false; }
        }
        return true;
    }

    private async Task<(int, string)> RunDockerAsync(string work, string sdkImage, CancellationToken ct,
        string script = "run.sh", DockerServiceHost? services = null)
    {
        // Shared NuGet cache across cases — containers are --rm, so without this every case
        // re-downloads all packages. Persisted on the host between runs.
        var nugetCache = Path.Combine(Path.GetTempPath(), "sharpbench", "nuget");
        Directory.CreateDirectory(nugetCache);

        var args = new List<string>
        {
            "run", "--rm",
            "-v", $"{work}:/io",
            "-v", $"{nugetCache}:/root/.nuget/packages",
        };
        if (services is not null)
            args.AddRange(services.RunArgs());
        args.Add(sdkImage);
        args.Add("bash");
        args.Add($"/io/{script}");

        return await RunProcessAsync("docker", args.ToArray(), ct, _timeoutSeconds);
    }

    private sealed record VerifyOutcome(bool Built, List<string> FailedP2P, string Feedback, string Tail);

    /// <summary>
    /// Legitimate SWE-agent feedback: build the repo (solver's non-test edits, NO test patch) and run the
    /// PASS_TO_PASS tests (which exist at base). Returns compile errors and any PASS_TO_PASS that now fail.
    /// FAIL_TO_PASS is held out — never run here — so this cannot leak the grader.
    /// </summary>
    private async Task<VerifyOutcome> VerifyAsync(string work, string src, string sdkImage, string framework,
        List<string> p2p, CancellationToken ct)
    {
        var voutHost = Path.Combine(work, "vout");
        if (Directory.Exists(voutHost)) Directory.Delete(voutHost, recursive: true);
        Directory.CreateDirectory(voutHost);

        var p2pFilter = string.Join("|", p2p.Select(t => $"FullyQualifiedName={t}"));
        var verifyProjects = TestProjectResolver.Resolve(src, p2p);
        var verifyPlan = verifyProjects.Count > 0 && _frameworkOverride is null
            ? RepoProbe.Detect(src, verifyProjects)
            : new RepoProbe.Plan(sdkImage, framework, -1);
        var verifySdk = _sdkImageOverride ?? verifyPlan.SdkImage;
        var verifyFramework = _frameworkOverride ?? verifyPlan.Framework;
        await File.WriteAllTextAsync(Path.Combine(work, "verify.sh"),
            TestRunScript.Verify(verifyFramework, p2pFilter, verifyProjects, ExtraMsBuildProps(src),
                SkiaNativeDeps.Needed(verifyProjects)), ct);

        var services = await DockerServiceHost.StartAsync(
            DockerServiceHost.NeedsCosmos(verifyProjects),
            DockerServiceHost.NeedsSqlServer(verifyProjects),
            ct, _timeoutSeconds);
        try
        {
            var (_, output) = await RunDockerAsync(work, verifySdk, ct, "verify.sh", services);
            var tail = string.Join("\n", output.Split('\n').TakeLast(60));

            var trx = Directory.GetFiles(voutHost, "*.trx");
            bool builtFromTrx = trx.Length > 0;
            bool builtFromExit = output.Contains("VERIFY_EXIT:0");
            var buildErr = FirstBuildError(output);

            if (p2p.Count > 0)
            {
                var rows = TrxParser.ReadResults(trx);
                var failed = p2p.Where(t => !TrxParser.Evaluate(t, rows).Passed).ToList();
                // If nothing built, the failures are really a build break.
                bool built = builtFromTrx && buildErr is null;
                return new VerifyOutcome(built, built ? failed : new(),
                    BuildFeedback(built, buildErr, built ? failed : new(), tail), tail);
            }
            else
            {
                bool built = builtFromExit && buildErr is null;
                return new VerifyOutcome(built, new(), BuildFeedback(built, buildErr, new(), tail), tail);
            }
        }
        finally
        {
            if (services is not null) await services.DisposeAsync();
        }
    }

    private static string BuildFeedback(bool built, string? buildErr, List<string> failedP2P, string tail)
    {
        var sb = new System.Text.StringBuilder();
        if (!built)
        {
            sb.AppendLine("The project did NOT build.");
            if (buildErr is not null) sb.AppendLine($"First error: {buildErr}");
            sb.AppendLine("Build output (tail):").AppendLine(tail);
        }
        else if (failedP2P.Count > 0)
        {
            sb.AppendLine("The build succeeded but these existing tests (which must keep passing) now fail:");
            foreach (var t in failedP2P) sb.AppendLine($"  - {t}");
            sb.AppendLine("Test output (tail):").AppendLine(tail);
        }
        return sb.ToString();
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string file, string[] args, CancellationToken ct, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await proc.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            lock (output) output.AppendLine($"[sharpbench] killed after {timeoutSeconds}s timeout");
            return (-1, output.ToString());
        }
        return (proc.ExitCode, output.ToString());
    }

    private static readonly System.Text.RegularExpressions.Regex BuildErrorRe =
        new(@": error ((?:CS|NU|MSB)\d+: .+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>First C#/NuGet/MSBuild error line from the container output, if any.</summary>
    private static string? FirstBuildError(string log)
    {
        var m = BuildErrorRe.Match(log);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static readonly System.Text.RegularExpressions.Regex DiffPathRe =
        new(@"^\+\+\+ b/(.+)$", System.Text.RegularExpressions.RegexOptions.Multiline
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Post-image paths a unified diff modifies (skipping deletions).</summary>
    private static IEnumerable<string> ExtractPaths(string patch) =>
        DiffPathRe.Matches(patch).Select(m => m.Groups[1].Value.Trim())
            .Where(p => p != "/dev/null").Distinct();

    private static string? ExtraMsBuildProps(string srcDir)
    {
        var props = new List<string>();
        if (SixLaborsBuildHelper.UsesPreviewBuild(srcDir))
            props.Add(SixLaborsBuildHelper.PreviewMsBuildProp);
        if (UsesCoverletInTests(srcDir))
            props.Add("-p:CollectCoverage=false");
        return props.Count == 0 ? null : string.Join(' ', props);
    }

    private static bool UsesCoverletInTests(string srcDir)
    {
        var targets = Path.Combine(srcDir, "eng", "Test.targets");
        return File.Exists(targets)
               && SafeRead(targets).Contains("<CollectCoverage>true</CollectCoverage>", StringComparison.Ordinal);
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    private static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* container-written files may be root-owned; leave them in temp */ }
    }
}
