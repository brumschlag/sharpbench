using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace SharpBench;

/// <summary>Reads SWE-Sharp-Bench cases from the CSV. Patches contain commas, quotes,
/// and embedded newlines, so a real CSV parser (CsvHelper) is required — not line splitting.</summary>
public static class DatasetLoader
{
    public static List<BenchCase> Load(string csvPath)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            // The dataset uses standard RFC-4180 quoting; be lenient about stray bad data.
            BadDataFound = null,
            MissingFieldFound = null,
        };

        using var reader = new StreamReader(csvPath);
        using var csv = new CsvReader(reader, config);
        return csv.GetRecords<BenchCase>().ToList();
    }
}
