namespace SharpBench;

/// <summary>Tests that require Windows (COM, Excel interop, etc.).</summary>
internal static class WindowsOnlyTest
{
    public static bool IsWindowsOnly(string fullyQualifiedName) =>
        fullyQualifiedName.Contains("ComCompatibility", StringComparison.OrdinalIgnoreCase)
        || fullyQualifiedName.Contains("Excel", StringComparison.OrdinalIgnoreCase)
        || fullyQualifiedName.Contains(".Com.", StringComparison.OrdinalIgnoreCase);
}
