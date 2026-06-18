using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpBench;

/// <summary>Ensures pinned <c>global.json</c> SDK versions can roll forward in Docker images.</summary>
internal static class GlobalJsonPatcher
{
    public static void PatchRollForward(string srcDir)
    {
        foreach (var path in Directory.EnumerateFiles(srcDir, "global.json", SearchOption.AllDirectories))
        {
            JsonNode? root;
            try { root = JsonNode.Parse(File.ReadAllText(path)); }
            catch { continue; }
            if (root?["sdk"] is not JsonObject sdk || sdk["version"] is null) continue;

            if (sdk["rollForward"] is null)
                sdk["rollForward"] = "latestFeature";
            else if (sdk["rollForward"]?.GetValue<string>() is "patch" or "disable")
                sdk["rollForward"] = "latestFeature";

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }
}
