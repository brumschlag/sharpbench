using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace SharpBench;

/// <summary>
/// A baseline solver: Claude attempts the fix in the "oracle-file" setting — it is told WHICH files
/// it may edit (the file list from the gold patch, NOT the fix), shown their full current contents and
/// the problem statement, and returns corrected file contents. The model never sees the gold patch or
/// the tests. This is a standard SWE-bench baseline (oracle retrieval); a real agent would discover the
/// files itself. Edits are written into the working tree for the evaluator to diff and test.
/// </summary>
public sealed partial class ClaudeSolver : ISolver
{
    private readonly AnthropicClient _client;
    public string Name => "claude(oracle)";

    public ClaudeSolver(AnthropicClient? client = null) => _client = client ?? new AnthropicClient();

    [GeneratedRegex(@"^\+\+\+ b/(.+)$", RegexOptions.Multiline)] private static partial Regex PostImagePath();

    public async Task SolveAsync(BenchCase c, string srcDir, string? feedback, CancellationToken ct = default)
    {
        // Oracle file set: the paths the gold patch touches (post-image side), minus deletions.
        var targets = PostImagePath().Matches(c.Patch)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(p => p != "/dev/null")
            .Distinct()
            .ToList();
        if (targets.Count == 0) throw new InvalidOperationException("Could not determine target files from gold patch.");

        // Read current contents of each target that exists on disk.
        var present = new List<(string Path, string Content)>();
        foreach (var rel in targets)
        {
            var abs = Path.Combine(srcDir, rel);
            if (File.Exists(abs)) present.Add((rel, await File.ReadAllTextAsync(abs, ct)));
        }
        if (present.Count == 0) throw new InvalidOperationException("None of the target files exist at base_commit.");

        var parameters = new MessageCreateParams
        {
            Model = Model.ClaudeOpus4_8,
            MaxTokens = 16000,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig
            {
                Effort = Effort.High,
                Format = new JsonOutputFormat { Schema = Schema },
            },
            System = SystemPrompt,
            Messages = [new() { Role = Role.User, Content = BuildPrompt(c, present) }],
        };

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);
        var json = response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("Solver returned no content.");

        var edits = JsonSerializer.Deserialize<SolverOutput>(json)?.Files ?? new();
        if (edits.Count == 0) throw new InvalidOperationException("Solver proposed no file edits.");

        // Write edits back. Restrict to the oracle set so the model can't touch tests or unrelated files.
        var allowed = present.Select(p => p.Path).ToHashSet();
        foreach (var e in edits)
        {
            if (!allowed.Contains(e.Path)) continue;
            var abs = Path.Combine(srcDir, e.Path);
            await File.WriteAllTextAsync(abs, e.NewContent, ct);
        }
    }

    private const string SystemPrompt =
        "You are an expert C#/.NET engineer fixing a reported bug. You are given the issue and the full " +
        "current contents of the file(s) you are allowed to edit. Return the COMPLETE corrected contents " +
        "of each file you change — not a diff, not a snippet. Make the smallest change that fixes the issue. " +
        "Do not edit test files. Do not add commentary.";

    private static string BuildPrompt(BenchCase c, List<(string Path, string Content)> files)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Repository\n{c.Repo}\n");
        sb.AppendLine($"# Issue\n{c.ProblemStatement}\n");
        if (!string.IsNullOrWhiteSpace(c.HintsText) && c.HintsText != "nan")
            sb.AppendLine($"# Hints\n{c.HintsText}\n");
        sb.AppendLine("# Files you may edit (full current contents)\n");
        foreach (var (path, content) in files)
            sb.AppendLine($"## {path}\n```csharp\n{content}\n```\n");
        sb.AppendLine("Return the complete corrected contents of each file you need to change to fix the issue.");
        return sb.ToString();
    }

    private sealed class SolverOutput
    {
        [JsonPropertyName("files")] public List<FileEdit> Files { get; set; } = new();
    }

    private sealed class FileEdit
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("new_content")] public string NewContent { get; set; } = "";
    }

    private static readonly Dictionary<string, JsonElement> Schema = JsonSerializer
        .Deserialize<Dictionary<string, JsonElement>>("""
        {
          "type": "object",
          "properties": {
            "files": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "new_content": { "type": "string" }
                },
                "required": ["path", "new_content"],
                "additionalProperties": false
              }
            }
          },
          "required": ["files"],
          "additionalProperties": false
        }
        """)!;
}
