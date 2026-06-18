namespace SharpBench;

/// <summary>
/// SkiaSharp on Linux needs system libraries (fontconfig, freetype) that are not in the
/// stock <c>mcr.microsoft.com/dotnet/sdk</c> images.
/// </summary>
internal static class SkiaNativeDeps
{
    public static bool Needed(IEnumerable<string>? repoRelativeProjects) =>
        repoRelativeProjects?.Any(p =>
            p.Contains("Skia", StringComparison.OrdinalIgnoreCase)) == true;

    public const string AptInstall =
        """
        if ! ldconfig -p 2>/dev/null | grep -q libfontconfig; then
          apt-get update -qq
          DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libfontconfig1 libfreetype6 >/dev/null 2>&1 || true
        fi
        """;
}
