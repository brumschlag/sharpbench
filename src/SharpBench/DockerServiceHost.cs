using System.Diagnostics;

namespace SharpBench;

/// <summary>Starts Cosmos DB / SQL Server sidecars on a shared Docker network for EF Core functional tests.</summary>
internal sealed class DockerServiceHost : IAsyncDisposable
{
    private const string CosmosImage = "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-latest";
    private const string SqlImage = "mcr.microsoft.com/mssql/server:2022-latest";
    private const string SqlPassword = "SharpBench!12345";

    private readonly string _network;
    private readonly string? _cosmosName;
    private readonly string? _sqlName;
    private readonly List<string> _containers = new();

    private DockerServiceHost(string network, string? cosmosName, string? sqlName)
    {
        _network = network;
        _cosmosName = cosmosName;
        _sqlName = sqlName;
    }

    public static bool NeedsCosmos(IEnumerable<string>? projects) =>
        projects?.Any(p => p.Contains("Cosmos.FunctionalTests", StringComparison.OrdinalIgnoreCase)) == true;

    public static bool NeedsSqlServer(IEnumerable<string>? projects) =>
        projects?.Any(p => p.Contains("SqlServer.FunctionalTests", StringComparison.OrdinalIgnoreCase)) == true;

    public static async Task<DockerServiceHost?> StartAsync(
        bool cosmos, bool sqlServer, CancellationToken ct, int timeoutSeconds)
    {
        if (!cosmos && !sqlServer) return null;

        var network = $"sharpbench-net-{Guid.NewGuid():N}";
        var host = new DockerServiceHost(
            network,
            cosmos ? $"sharpbench-cosmos-{Guid.NewGuid():N}" : null,
            sqlServer ? $"sharpbench-mssql-{Guid.NewGuid():N}" : null);

        await RunDockerAsync(new[] { "network", "create", network }, ct, timeoutSeconds);

        try
        {
            if (cosmos)
            {
                await host.StartCosmosAsync(ct, timeoutSeconds);
                host._containers.Add(host._cosmosName!);
            }

            if (sqlServer)
            {
                await host.StartSqlServerAsync(ct, timeoutSeconds);
                host._containers.Add(host._sqlName!);
            }

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public IReadOnlyList<string> RunArgs()
    {
        var args = new List<string> { "--network", _network };
        foreach (var (key, value) in EnvVars())
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }
        return args;
    }

    private IEnumerable<KeyValuePair<string, string>> EnvVars()
    {
        if (_cosmosName is not null)
            yield return new("Test__Cosmos__DefaultConnection", $"http://{_cosmosName}:8081");
        if (_sqlName is not null)
            yield return new("Test__SqlServer__DefaultConnection",
                $"Server={_sqlName},1433;Database=master;User Id=sa;Password={SqlPassword};TrustServerCertificate=True;Encrypt=False");
    }

    private async Task StartCosmosAsync(CancellationToken ct, int timeoutSeconds)
    {
        await RunDockerAsync(new[]
        {
            "run", "-d", "--name", _cosmosName!, "--network", _network,
            "-e", "PROTOCOL=http",
            "-e", $"GATEWAY_PUBLIC_ENDPOINT={_cosmosName}",
            CosmosImage,
        }, ct, timeoutSeconds);

        await WaitForAsync(
            new[] { "run", "--rm", "--network", _network, "curlimages/curl:8.5.0",
                "curl", "-sf", $"http://{_cosmosName}:8081/" },
            ct, timeoutSeconds, pollSeconds: 2, maxAttempts: 90);
    }

    private async Task StartSqlServerAsync(CancellationToken ct, int timeoutSeconds)
    {
        await RunDockerAsync(new[]
        {
            "run", "-d", "--name", _sqlName!, "--network", _network,
            "-e", "ACCEPT_EULA=Y",
            "-e", $"MSSQL_SA_PASSWORD={SqlPassword}",
            "-e", "MSSQL_PID=Developer",
            SqlImage,
        }, ct, timeoutSeconds);

        var probe = new[]
        {
            "run", "--rm", "--network", _network, "alpine:3.20",
            "sh", "-c", $"nc -z {_sqlName} 1433"
        };
        await WaitForAsync(probe, ct, timeoutSeconds, pollSeconds: 3, maxAttempts: 60);
    }

    private static async Task WaitForAsync(
        string[] probeArgs, CancellationToken ct, int timeoutSeconds, int pollSeconds, int maxAttempts)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var (code, _) = await RunDockerAsync(probeArgs, ct, timeoutSeconds);
            if (code == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct);
        }
        throw new InvalidOperationException("Timed out waiting for service container to become ready.");
    }

    private static Task<(int, string)> RunDockerAsync(string[] args, CancellationToken ct, int timeoutSeconds)
    {
        var all = new List<string> { "docker" };
        all.AddRange(args);
        return RunProcessAsync("docker", all.Skip(1).ToArray(), ct, timeoutSeconds);
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
            return (-1, output.ToString());
        }
        return (proc.ExitCode, output.ToString());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var name in _containers)
        {
            try { await RunDockerAsync(new[] { "rm", "-f", name }, CancellationToken.None, 30); }
            catch { /* best effort */ }
        }
        try { await RunDockerAsync(new[] { "network", "rm", _network }, CancellationToken.None, 30); }
        catch { /* best effort */ }
    }
}
