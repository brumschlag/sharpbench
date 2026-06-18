using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace SharpBench;

/// <summary>
/// LLM-as-judge backed by Claude. Given a case and a candidate patch, it decides whether the
/// patch correctly resolves the issue, using the gold patch and gating tests as reference.
///
/// The "what to judge" lives entirely in <see cref="BuildPrompt"/> — swap that to re-target
/// (e.g. score difficulty, grade against a rubric, compare two candidates).
/// </summary>
public sealed class ClaudeJudge : IJudge
{
    private readonly AnthropicClient _client;

    public ClaudeJudge(AnthropicClient? client = null) => _client = client ?? new AnthropicClient();

    public async Task<Verdict> JudgeAsync(BenchCase c, string candidatePatch, CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = Model.ClaudeOpus4_8,
            MaxTokens = 8000,
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = VerdictSchema } },
            System = SystemPrompt,
            Messages = [new() { Role = Role.User, Content = BuildPrompt(c, candidatePatch) }],
        };

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);

        // With output_config.format set, the first text block is guaranteed valid JSON for the schema.
        var json = response.Content
            .Select(b => b.Value).OfType<TextBlock>()
            .Select(t => t.Text).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(json))
            return new Verdict { Result = "uncertain", Reasoning = "Empty response from judge." };

        return JsonSerializer.Deserialize<Verdict>(json) ?? new Verdict { Reasoning = "Failed to parse verdict JSON." };
    }

    private const string SystemPrompt =
        "You are a meticulous senior C#/.NET engineer acting as an automated grader for the " +
        "SWE-Sharp-Bench benchmark. You judge whether a candidate code patch correctly resolves a " +
        "reported issue. You reason about the diff's semantics, not its surface text. Be strict: a " +
        "patch is only 'correct' if it would make the failing tests pass without breaking the passing ones.";

    private static string BuildPrompt(BenchCase c, string candidatePatch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Repository\n{c.Repo} @ {c.BaseCommit}\n");
        sb.AppendLine($"# Problem statement\n{c.ProblemStatement}\n");
        if (!string.IsNullOrWhiteSpace(c.HintsText) && c.HintsText != "nan")
            sb.AppendLine($"# Hints\n{c.HintsText}\n");
        sb.AppendLine($"# Gating tests\nFAIL_TO_PASS (must flip to passing): {c.FailToPass}\nPASS_TO_PASS (must stay passing): {c.PassToPass}\n");
        sb.AppendLine($"# Test diff (defines the gating tests)\n```diff\n{c.TestPatch}\n```\n");
        sb.AppendLine($"# Reference (gold) patch — a known-correct fix\n```diff\n{c.Patch}\n```\n");
        sb.AppendLine($"# Candidate patch — THE ONE YOU ARE JUDGING\n```diff\n{candidatePatch}\n```\n");
        sb.AppendLine(
            "Judge the CANDIDATE patch. Decide:\n" +
            "- verdict: \"correct\" if it resolves the problem and would satisfy the gating tests; " +
            "\"incorrect\" if it does not; \"uncertain\" if you genuinely cannot tell.\n" +
            "- confidence: 0.0-1.0.\n" +
            "- addresses_problem: does it target the right root cause described in the problem statement?\n" +
            "- likely_passes_tests: would FAIL_TO_PASS flip and PASS_TO_PASS stay green?\n" +
            "- reasoning: 2-4 sentences justifying the call, referencing specific code where relevant.");
        return sb.ToString();
    }

    private static readonly Dictionary<string, JsonElement> VerdictSchema = JsonSerializer
        .Deserialize<Dictionary<string, JsonElement>>("""
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["correct", "incorrect", "uncertain"] },
            "confidence": { "type": "number" },
            "addresses_problem": { "type": "boolean" },
            "likely_passes_tests": { "type": "boolean" },
            "reasoning": { "type": "string" }
          },
          "required": ["verdict", "confidence", "addresses_problem", "likely_passes_tests", "reasoning"],
          "additionalProperties": false
        }
        """)!;
}
