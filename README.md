# SharpBench

A small C# (.NET 10) harness for running [microsoft/SWE-Sharp-Bench](https://huggingface.co/datasets/microsoft/SWE-Sharp-Bench)
cases. Each case is a real C#/.NET GitHub issue plus the gold fix and the tests that gate it. Two
ways to evaluate a *candidate* patch:

- **`execute` mode** — ground truth, the real SWE-bench method. Runs the gating tests in Docker and
  checks `FAIL_TO_PASS` flip + `PASS_TO_PASS` hold. Deterministic.
- **`judge` mode** — an **LLM-as-judge** (Claude) reasons about the diff. Cheap proxy, no build/test.

## Layout

| File | Role |
|------|------|
| `BenchCase.cs` | Maps the dataset columns (`repo`, `patch`, `test_patch`, `problem_statement`, `FAIL_TO_PASS`, …). |
| `DatasetLoader.cs` | Reads `swe-sharp-bench.csv` via CsvHelper (patches contain commas/quotes/newlines). |
| `IJudge.cs` / `ClaudeJudge.cs` | LLM judge. `ClaudeJudge` calls `claude-opus-4-8` with a structured-JSON verdict. |
| `Verdict.cs` | LLM judge output: `verdict`, `confidence`, `addresses_problem`, `likely_passes_tests`, `reasoning`. |
| `ExecutionEvaluator.cs` | Ground-truth runner: fetch repo@commit, `git apply` patches, `dotnet test` in Docker. |
| `TrxParser.cs` / `EvaluationResult.cs` | Parse VSTest `.trx`; `Resolved = all FAIL_TO_PASS pass AND all PASS_TO_PASS pass`. |
| `Program.cs` | CLI: `--mode judge|execute` → write `results/*.json`. |

## Setup

```bash
dotnet build
export ANTHROPIC_API_KEY=...                 # judge / claude solver only
```

The benchmark dataset is vendored at `data/swe-sharp-bench.csv`. See [DATASET.md](DATASET.md) for
attribution and how to refresh from Hugging Face.

## Run — execute mode (ground truth)

Requires **Docker** and local **git**. The SDK image and target framework are **auto-detected per
case** — no manual flags needed.

```bash
dotnet run --project src/SharpBench -- --mode execute --instance ardalis__cleanarchitecture-546
dotnet run --project src/SharpBench -- --mode execute --instance serilog__serilog-2116
dotnet run --project src/SharpBench -- --mode execute --repo CleanArchitecture --limit 3
```

How it works per case:
1. **Host:** `git fetch --depth 1 origin <base_commit>` → checkout → `git apply` candidate + test patches.
2. **Detect** (`RepoProbe`): read `global.json` and the test `.csproj` files → choose a target framework
   (the major the SDK pins, else the newest the tests support) → SDK image = `sdk:<framework-major>`.
3. **Container:** `dotnet test --framework <tfm> --filter <named tests>` in that image, TRX logger.
4. `resolved = all FAIL_TO_PASS pass AND all PASS_TO_PASS pass`.

Flags: `--instance ID` · `--repo SUBSTR` · `--limit N` · `--out DIR` ·
`--sdk-image IMG` / `--framework TFM` (override detection when it guesses wrong).
Writes `results/<id>.exec.json` (resolved, detected image/fw, per-test outcomes) and `<id>.exec.log`.

> First run for an SDK major pulls the image and restores NuGet — give it a few minutes.
> `--framework` pins one TFM so multi-targeted test projects only run the runtime the image has.

## Run — judge mode (LLM proxy)

```bash
dotnet run --project src/SharpBench -- --repo CleanArchitecture --limit 2
dotnet run --project src/SharpBench -- --limit 10            # first 10 cases, any repo
```

Judge flags: `--data PATH` · `--limit N` · `--repo SUBSTR` · `--out DIR`.

In both modes the **candidate patch = the gold patch** by default, so a run is a smoke test —
`execute` should report `resolved=true`; `judge` should report `"correct"`. To evaluate a real
solver, see below.

## Solvers (`--solver`)

Pluggable via `ISolver`: each edits the checked-out working tree; the evaluator captures `git diff` as
the candidate patch and runs the gating tests. Pick with `--solver` in execute mode (default `gold`):

| `--solver` | What it does | Setting |
|------------|--------------|---------|
| `gold` | Applies the dataset's gold patch | Harness self-check (upper bound) |
| `claude` | `ClaudeSolver`: Claude rewrites the files it's *told* it may edit (from the gold file list) | **oracle-file**, single-shot |
| `pi [--model anthropic/<id>] [--max-rounds N]` | The [Pi](https://pi.dev) coding agent, headless (`pi -p`) — gets only the issue, explores with read/grep/edit | **agentic**, no oracle, **iterative** |

### Pi's verify→retry loop (SWE-agent style)

With `--max-rounds N` (default 3), Pi iterates: **attempt → build + run only `PASS_TO_PASS` in the detected
Docker image (NO `test_patch`) → on a compile error or `PASS_TO_PASS` regression, feed it back → retry.**
The held-out `FAIL_TO_PASS` tests are **never** run during verification, so the grader can't leak. The loop
stops early once the candidate builds clean with no regressions. `Rounds` is recorded per case.

Note: this can't rescue cases whose only failure appears *after* `test_patch` is applied (e.g. an API the
hidden test needs) — no compliant self-verification sees those. It helps cases where the agent introduces a
*visible* compile error or breaks an existing test.

`pi` requires the CLI (`npm i -g @earendil-works/pi-coding-agent`) and `ANTHROPIC_API_KEY`.
`results/<id>.candidate.patch` = the solver's diff; `results/<id>.pi.log` = Pi's transcript.
When a candidate compiles alone but breaks the test project, the evaluator reports
`candidate did not compile: <CS/NU/MSB error>` rather than a bare `MISSING`.

```bash
dotnet run --project src/SharpBench -- --mode execute --instance ardalis__cleanarchitecture-546 --solver pi
```

## Results so far

**Gold solver (harness self-check, Linux + Docker)** — **149 / 150** cases resolve with the vendored
dataset. The one expected skip is `devlooped__moq-1076` (Windows COM / `net48` interop).

Spot checks from earlier development:
- Serilog: **12/12** resolved. CleanArchitecture: **3/3** (546, 530*, 918). Polly-2164 ✅.

**Claude solver (oracle-file baseline)** on CleanArchitecture:
- 546 ✅ · 918 ✅ · 530 ❌ → **2/3**.
  - 546: Claude produced a *different* valid fix than gold (real tests credited it).
  - 918: Claude independently reproduced the gold change (FluentAssertions→Shouldly migration).
  - 530: a 10-file refactor (incl. build props). The single-shot full-file rewrite produced an
    incomplete edit → build failed (`NU1010`). This is the baseline solver's limitation, not the harness.

**Pi agentic solver** on CleanArchitecture-546: ✅ resolved. A first attempt added only a parameterless
constructor → the hidden test (which calls the `string` ctor) failed to compile → the evaluator reported
`candidate did not compile: CS0122`. Tightening the prompt ("don't reduce accessibility; keep the
solution compiling") produced a complete fix that resolves. Good illustration of the eval catching an
incomplete agentic fix via compilation, not just test outcomes.

\* 530 under gold also needs the env note below.

### Environment drift (`NuGetAudit=false`)

Newer SDK images run NuGet's vulnerability **audit**, flagging transitive-dependency advisories
(`NU1901/NU1902`) that didn't exist when a case was authored. Repos that `TreatWarningsAsErrors` then
**fail to build for reasons unrelated to the patch** (hit on CleanArchitecture-918: OpenTelemetry/MailKit
advisories). The runner passes `-p:NuGetAudit=false` to reproduce the original build behavior — the same
problem SWE-bench avoids with frozen per-instance images. Doesn't affect test logic.

Detection + execution cover the common cases. Heavier repos (Avalonia, EFCore, jellyfin) may need more
time/memory or native deps, and Windows-only TFMs (`net48`) can't run on Linux SDK images. Override with
`--sdk-image` / `--framework` if `RepoProbe` guesses wrong.

## Dataset

Vendored at `data/swe-sharp-bench.csv` — 150 cases, single `train` split, SWE-bench schema.
Upstream source and citation: [DATASET.md](DATASET.md).
