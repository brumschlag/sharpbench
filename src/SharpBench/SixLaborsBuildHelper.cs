namespace SharpBench;

/// <summary>
/// SixLabors repos gate preview language features and extra TFMs behind
/// <c>SIXLABORS_TESTING_PREVIEW</c> (see their CI workflows). Without it, ImageSharp
/// sources using <c>[UnscopedRef]</c> fail to compile; with it, SDK 6 cannot restore net7.0.
/// </summary>
internal static class SixLaborsBuildHelper
{
    public static bool UsesPreviewBuild(string srcDir)
    {
        var props = Path.Combine(srcDir, "Directory.Build.props");
        if (!File.Exists(props)) return false;
        try
        {
            return File.ReadAllText(props).Contains("SIXLABORS_TESTING_PREVIEW", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public const string PreviewMsBuildProp = "-p:SIXLABORS_TESTING_PREVIEW=true";

    /// <summary>SDK major floor when preview build is required (net7.0 appears in TFMs).</summary>
    public const int PreviewSdkMajorFloor = 7;
}
