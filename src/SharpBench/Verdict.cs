using System.Text.Json.Serialization;

namespace SharpBench;

/// <summary>The judge's structured assessment of a candidate patch for one case.</summary>
public sealed class Verdict
{
    [JsonPropertyName("verdict")]
    public string Result { get; set; } = "uncertain"; // "correct" | "incorrect" | "uncertain"

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } // 0.0 - 1.0

    [JsonPropertyName("addresses_problem")]
    public bool AddressesProblem { get; set; }

    [JsonPropertyName("likely_passes_tests")]
    public bool LikelyPassesTests { get; set; }

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";
}

/// <summary>A verdict paired with the case it was rendered for.</summary>
public sealed class JudgedCase
{
    public string InstanceId { get; set; } = "";
    public string Repo { get; set; } = "";
    public Verdict Verdict { get; set; } = new();
}
