namespace SharpBench;

/// <summary>Generates container bash scripts for dotnet test/build.</summary>
internal static class TestRunScript
{
    private static string MsBuildProps(string? extra = null) =>
        "-p:NuGetAudit=false -p:RunAnalyzersDuringBuild=false -p:TreatWarningsAsErrors=false"
        + (string.IsNullOrEmpty(extra) ? "" : " " + extra);

    /// <summary>
    /// Run gating tests. Prefer <c>test/**/*.csproj</c> so benchmarks/samples in the solution
    /// are not restored (avoids NETSDK1045 when benchmarks target a newer SDK than the tests).
    /// </summary>
    public static string Test(string framework, string filter, IReadOnlyList<string>? projects = null,
        string? extraMsBuildProps = null, bool installSkiaNativeDeps = false,
        IReadOnlyDictionary<string, string>? projectFilters = null)
    {
        var props = MsBuildProps(extraMsBuildProps);
        var filterLookup = BuildFilterLookup(projectFilters);
        var perTest = PerTestLoop(framework, "$proj_filter", "/io/out", "40", props);
        return string.Join('\n',
            Preamble(framework, installSkiaNativeDeps),
            Cleanup(projects),
            ProjectSetup(projects),
            filterLookup,
            """
            if [ ${#projects[@]} -eq 0 ]; then
            """,
            $"  echo \">> dotnet test --framework {framework} (solution fallback)\"",
            $"  dotnet test --framework {framework} --filter \"{filter}\" {props} \\",
            "    --logger trx --results-directory /io/out 2>&1 | tail -80 || true",
            "else",
            "  for proj in \"${projects[@]}\"; do",
            "    proj_filter=\"${filters[$proj]:-" + filter + "}\"",
            "    if [[ -z \"$proj_filter\" ]]; then continue; fi",
            "    if [[ \"$proj\" == *Integrated* ]]; then",
            perTest,
            "    else",
            $"      echo \">> dotnet test $proj --framework {framework} --filter $proj_filter\"",
            $"      rm -rf /io/src/artifacts/obj/$(basename \"$proj\" .csproj) /io/src/artifacts/bin/$(basename \"$proj\" .csproj) 2>/dev/null || true",
            $"      dotnet restore \"$proj\" -p:TargetFramework={framework} {props} >/dev/null 2>&1 || true",
            $"      dotnet test \"$proj\" --framework {framework} --filter \"$proj_filter\" {props} \\",
            "        --logger trx --results-directory /io/out 2>&1 | tail -80 || true",
            "    fi",
            "  done",
            "fi",
            "echo \">> done\"");
    }

    private static string BuildFilterLookup(IReadOnlyDictionary<string, string>? projectFilters)
    {
        if (projectFilters is not { Count: > 0 }) return "declare -A filters=()";
        var lines = projectFilters.Select(kv => $"filters[\"{kv.Key}\"]='{kv.Value}'");
        return "declare -A filters\n" + string.Join('\n', lines);
    }

    public static string Verify(string framework, string p2pFilter, IReadOnlyList<string>? projects = null,
        string? extraMsBuildProps = null, bool installSkiaNativeDeps = false)
    {
        var props = MsBuildProps(extraMsBuildProps);
        return string.Join('\n',
            Preamble(framework, installSkiaNativeDeps),
            Cleanup(projects),
            "rm -rf /io/vout && mkdir -p /io/vout",
            ProjectSetup(projects),
            $"if [ -n \"{p2pFilter}\" ]; then",
            "  if [ ${#projects[@]} -eq 0 ]; then",
            "    echo \">> verify: dotnet test PASS_TO_PASS (solution fallback)\"",
            $"    dotnet test --framework {framework} --filter \"{p2pFilter}\" {props} \\",
            "      --logger trx --results-directory /io/vout > /io/vout/log.txt 2>&1",
            "  else",
            "    : > /io/vout/log.txt",
            "    for proj in \"${projects[@]}\"; do",
            "      echo \">> verify: dotnet test $proj\" >> /io/vout/log.txt",
            $"      dotnet test \"$proj\" --framework {framework} --filter \"{p2pFilter}\" {props} \\",
            "        --logger trx --results-directory /io/vout >> /io/vout/log.txt 2>&1 || true",
            "    done",
            "  fi",
            "  echo \"VERIFY_EXIT:$?\"",
            "else",
            "  echo \">> verify: dotnet build (no PASS_TO_PASS tests)\"",
            "  if [ ${#projects[@]} -eq 0 ]; then",
            $"    dotnet build {props} > /io/vout/log.txt 2>&1",
            "  else",
            "    : > /io/vout/log.txt",
            "    for proj in \"${projects[@]}\"; do",
            "      echo \">> verify: dotnet build $proj\" >> /io/vout/log.txt",
            $"      dotnet build \"$proj\" {props} >> /io/vout/log.txt 2>&1 || true",
            "    done",
            "  fi",
            "  echo \"VERIFY_EXIT:$?\"",
            "fi",
            "tail -150 /io/vout/log.txt");
    }

    private static string Preamble(string framework, bool installSkiaNativeDeps = false)
    {
        var rollForward = NeedsRuntimeRollForward(framework)
            ? "export DOTNET_ROLL_FORWARD=LatestMajor\n"
            : "";
        var skia = installSkiaNativeDeps ? SkiaNativeDeps.AptInstall + "\n" : "";
        return rollForward + skia +
            """
            export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
            cd /io/src
            """;
    }

    /// <summary>SDK 8 images ship only the latest runtime; older TFMs need roll-forward to execute tests.</summary>
    private static bool NeedsRuntimeRollForward(string framework) =>
        framework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
        || framework.StartsWith("net5", StringComparison.OrdinalIgnoreCase)
        || framework.StartsWith("net6", StringComparison.OrdinalIgnoreCase)
        || framework.StartsWith("net7", StringComparison.OrdinalIgnoreCase);

    private static string Cleanup(IReadOnlyList<string>? projects) =>
        projects is { Count: > 0 }
            ? string.Join('\n', projects.SelectMany(p =>
            {
                var dir = $"$(dirname \"{p}\")";
                var name = Path.GetFileNameWithoutExtension(p.Replace('\\', '/'));
                return new[]
                {
                    $"rm -rf /io/src/{dir}/obj /io/src/{dir}/bin 2>/dev/null; true",
                    $"rm -rf /io/src/artifacts/obj/{dir} /io/src/artifacts/bin/{dir} 2>/dev/null; true",
                    $"rm -rf /io/src/artifacts/obj/{name} /io/src/artifacts/bin/{name} 2>/dev/null; true",
                };
            }))
            : GlobalCleanup + "\nrm -rf /io/src/artifacts/obj /io/src/artifacts/bin 2>/dev/null; true";

    private const string GlobalCleanup =
        """
        find . -type d \( -name obj -o -name bin \) -prune -print0 | xargs -0 rm -rf 2>/dev/null; true
        """;

    private static string ProjectSetup(IReadOnlyList<string>? projects) =>
        projects is { Count: > 0 }
            ? "projects=(" + string.Join(' ', projects.Select(p => $"\"{p}\"")) + ")"
            : FindProjects();

    private static string FindProjects() =>
        $"""
        projects=($(find test tests -name '*.csproj' 2>/dev/null \
          | grep -Ev '{TestProjectFilter.GrepExcludePattern}' \
          | sort))
        """;

    /// <summary>
    /// HttpListener-based integration tests start the server on a background thread; one test per
    /// process avoids startup races when multiple tests share a filter.
    /// </summary>
    private static string PerTestLoop(string framework, string filterExpr, string trxDir, string tailLines,
        string msBuildProps) =>
        string.Join('\n',
            $"      names=($(echo \"{filterExpr}\" | sed 's/FullyQualifiedName=//g' | tr '|' '\\n'))",
            "      for name in \"${names[@]}\"; do",
            $"        echo \">> dotnet test $proj --framework {framework} --filter FullyQualifiedName=$name\"",
            $"        dotnet test \"$proj\" --framework {framework} --filter \"FullyQualifiedName=$name\" {msBuildProps} \\",
            $"          --logger trx --results-directory {trxDir} 2>&1 | tail -{tailLines} || true",
            "      done");
}
