# SharpBench

A C# harness for running [SWE-Sharp-Bench](https://huggingface.co/datasets/microsoft/SWE-Sharp-Bench) cases on
Linux with Docker. Each case is a real GitHub issue from a .NET open-source repository, paired with a gold
patch, a test patch, and the gating tests that define success.

SharpBench supports two evaluation modes:

| Mode | Method | Use case |
|------|--------|----------|
| **`execute`** | Run `dotnet test` in a per-case SDK container | Ground-truth grading (SWE-bench semantics) |
| **`judge`** | LLM reviews the candidate diff | Fast, approximate proxy without building |

The dataset is vendored at `data/swe-sharp-bench.csv` (150 cases). See [DATASET.md](DATASET.md) for attribution
and refresh instructions.

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Builds the harness (`net10.0`) |
| [Docker](https://docs.docker.com/get-docker/) | Required for `execute` mode |
| `git` | Shallow-clones upstream repos at each case's `base_commit` |
| Disk space | Scratch clones and NuGet caches live under `$TMPDIR/sharpbench` (or the system temp directory) |

For `judge` mode, `claude` solver, or `pi` solver, set `ANTHROPIC_API_KEY` in the environment.

## Quick start

```bash
git clone https://github.com/brumschlag/sharpbench.git
cd sharpbench
dotnet build

# Smoke test: apply the gold patch and run gating tests
dotnet run --project src/SharpBench -- --mode execute --instance ardalis__cleanarchitecture-546

# Full harness self-check (149/150 expected on Linux; see Validation)
TMPDIR=/var/tmp/sharpbench dotnet run --project src/SharpBench -c Release -- \
  --mode execute --all --solver gold --skip-done --timeout 900
```

Run all commands from the repository root, or pass `--data <path>` to point at a different CSV.

## Evaluation modes

### Execute mode (ground truth)

Execute mode is the authoritative evaluator. For each case it:

1. Shallow-fetches the upstream repository at `base_commit`.
2. Applies a **candidate patch** (from a solver, or the gold patch by default).
3. Applies the dataset's **test patch** (hidden tests the solver must not edit).
4. Detects the SDK image and target framework from `global.json` and test project files.
5. Runs only the named gating tests inside a matching `mcr.microsoft.com/dotnet/sdk` container.
6. Reports **resolved** when all `FAIL_TO_PASS` tests pass and all `PASS_TO_PASS` tests remain green.

```bash
# Single case
dotnet run --project src/SharpBench -- --mode execute --instance serilog__serilog-2116

# Filter by repository name
dotnet run --project src/SharpBench -- --mode execute --repo CleanArchitecture --limit 3

# Full dataset (resume-friendly)
dotnet run --project src/SharpBench -c Release -- \
  --mode execute --all --solver gold --skip-done --timeout 900
```

**Execute flags**

| Flag | Description |
|------|-------------|
| `--instance ID` | Run one case (e.g. `ardalis__cleanarchitecture-546`) |
| `--repo SUBSTR` | Run cases whose `repo` column contains `SUBSTR` |
| `--all` | Run all 150 cases (smallest repos first) |
| `--skip-done` | Skip cases that already have `results/<id>.exec.json` |
| `--solver gold\|claude\|pi` | Patch source (default: `gold`) |
| `--timeout SEC` | Per-case timeout in seconds (default: 900) |
| `--sdk-image IMG` | Override auto-detected SDK image |
| `--framework TFM` | Override auto-detected target framework (e.g. `net8.0`) |
| `--max-rounds N` | Iterative solver attempts (default: 3; `pi` only) |
| `--model ID` | Model for `pi` solver (default: `anthropic/claude-opus-4-8`) |
| `--out DIR` | Output directory (default: `results`) |
| `--data PATH` | Dataset CSV path (default: `data/swe-sharp-bench.csv`) |

The first run for a given SDK major pulls the container image and restores NuGet packages. Heavy repositories
(Avalonia, EF Core, Jellyfin) may require substantially more time and memory.

### Judge mode (LLM proxy)

Judge mode sends the problem statement and candidate patch to Claude and returns a structured verdict. It does
not build or run tests. Judge mode is the **default** when `--mode` is omitted.

```bash
dotnet run --project src/SharpBench -- --mode judge --repo CleanArchitecture --limit 2
dotnet run --project src/SharpBench -- --limit 10
```

Judge flags: `--data PATH` · `--limit N` (default: 3) · `--repo SUBSTR` · `--out DIR`.

By default the candidate patch is the gold patch, so judge mode acts as a smoke test of the judge itself.
Swap in a solver-generated patch to evaluate model output without executing tests.

## Solvers

Solvers implement `ISolver`: they edit the checked-out working tree, and the evaluator captures `git diff` as
the candidate patch before applying the test patch and grading.

| `--solver` | Description | Setting |
|------------|-------------|---------|
| `gold` | Applies the dataset gold patch | Harness upper bound |
| `claude` | Single-shot rewrite of files listed in the gold patch | Oracle-file baseline |
| `pi` | [Pi](https://pi.dev) coding agent (`pi -p`); explores the repo with read/grep/edit | Agentic, no oracle |

```bash
dotnet run --project src/SharpBench -- \
  --mode execute --instance ardalis__cleanarchitecture-546 --solver pi --max-rounds 3
```

`pi` requires the CLI (`npm i -g @earendil-works/pi-coding-agent`) and `ANTHROPIC_API_KEY`.

### Iterative verification (`pi`)

When `--max-rounds` is greater than 1, Pi runs a verify-and-retry loop between attempts:

1. Solve.
2. Build and run only `PASS_TO_PASS` tests (no test patch applied during verification).
3. On compile failure or regression, feed the error back and retry.

Held-out `FAIL_TO_PASS` tests are never run during verification, so the grader cannot leak. The loop stops
early when the candidate builds cleanly with no regressions, or when the round limit is reached.

This helps when the agent introduces visible compile errors or breaks existing tests. It cannot recover from
failures that only appear after the test patch is applied.

## Resolution criteria

A case is **resolved** when:

- The test project builds and produces TRX output, and
- Every test in `FAIL_TO_PASS` passes, and
- Every test in `PASS_TO_PASS` passes.

This matches SWE-bench semantics: fix the failing tests without regressing the suite.

## Output artifacts

All output is written to `results/` (gitignored). Typical files per case:

| File | Mode | Contents |
|------|------|----------|
| `<id>.exec.json` | execute | `resolved`, SDK image, framework, per-test outcomes, duration |
| `<id>.exec.log` | execute | Host setup log and container `dotnet test` output |
| `<id>.candidate.patch` | execute | Solver-produced diff (`git diff`) |
| `<id>.pi.log` | execute (`pi`) | Pi agent transcript |
| `<id>.json` | judge | Structured verdict |
| `_exec_summary.json` | execute | Batch summary for the current run |
| `_summary.json` | judge | Batch summary for the current run |

Execute mode exits `0` when all cases in the batch resolve, `2` when any fail.

## Platform and environment notes

**Linux is the primary target.** The harness auto-detects SDK images and target frameworks per case. Cases
that require Windows-only APIs (COM interop, `net48`) are reported as non-evaluable on Linux.

**NuGet audit drift.** Newer SDK images enable NuGet vulnerability auditing (`NU1901`/`NU1902`). Repositories
with `TreatWarningsAsErrors` may fail to build for advisory warnings unrelated to the patch. The runner passes
`-p:NuGetAudit=false` to match the build environment at case authoring time.

**Repository-specific handling** includes:

- EF Core functional tests: Cosmos DB and SQL Server sidecars on a shared Docker network.
- Avalonia / Skia: native dependency packages installed in the container.
- SixLabors.ImageSharp: preview SDK workaround when required.
- Nerdbank.GitVersioning: disabled for deterministic builds.
- CRLF repositories: `git apply` retried with `--ignore-whitespace`.
- Empty `test_patch` entries: skipped rather than failing apply.
- Coverlet: disabled when `eng/Test.targets` sets `CollectCoverage=true`.

Override `--sdk-image` or `--framework` when auto-detection selects an incompatible TFM for a multi-targeted
test project.

## Validation

Gold-solver validation on Linux with Docker (vendored dataset):

| Result | Detail |
|--------|--------|
| **149 / 150 resolved** | Full harness self-check |
| 1 expected skip | `devlooped__moq-1076` — Windows COM / `net48` interop |

Representative per-repo spot checks during development: Serilog 12/12, CleanArchitecture 3/3, Polly cases
including the corrected `polly-1472` gold patch in the vendored CSV.

### Solver baselines (illustrative)

These are exploratory runs, not benchmark scores:

- **Claude (oracle-file)** on CleanArchitecture: 2/3 (546, 918 resolved; 530 failed on an incomplete
  multi-file refactor).
- **Pi (agentic)** on CleanArchitecture-546: resolved after iterative verification caught a compile error
  from an incomplete first attempt.

## Project structure

```
sharpbench/
├── data/swe-sharp-bench.csv     # Vendored benchmark (150 cases)
├── src/SharpBench/
│   ├── Program.cs               # CLI entry point
│   ├── DatasetLoader.cs         # CSV loader (CsvHelper)
│   ├── ExecutionEvaluator.cs    # Docker-based ground-truth runner
│   ├── RepoProbe.cs             # SDK image and TFM detection
│   ├── DockerServiceHost.cs     # EF Core Cosmos / SQL Server sidecars
│   ├── TestProjectResolver.cs   # Map tests → project files
│   ├── TestRunScript.cs         # Container test script generation
│   ├── TrxParser.cs             # VSTest TRX parsing
│   ├── GoldSolver.cs            # Apply dataset patch
│   ├── ClaudeSolver.cs          # Oracle-file LLM solver
│   ├── PiSolver.cs              # Agentic Pi solver
│   └── ClaudeJudge.cs           # LLM-as-judge
├── DATASET.md                   # Dataset attribution
└── LICENSE                      # MIT
```

## Dataset

The benchmark is vendored at `data/swe-sharp-bench.csv` — 150 cases, single `train` split, SWE-bench schema.
Upstream source, citation, and refresh instructions: [DATASET.md](DATASET.md).

## License

SharpBench harness code is licensed under the [MIT License](LICENSE). The vendored dataset contains third-party
code excerpts from upstream repositories; see [DATASET.md](DATASET.md) for the SWE-Sharp-Bench citation.
