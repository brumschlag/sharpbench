using System.Xml.Linq;

namespace SharpBench;

/// <summary>Reads VSTest .trx result files and reports per-test pass/fail.</summary>
public static class TrxParser
{
    /// <summary>Returns (fullyQualifiedName-ish testName → passed?) for every result across all trx files.
    /// A theory produces several rows like <c>Ns.Class.Method(arg: 1)</c>; we keep each row.</summary>
    public static List<(string Name, bool Passed)> ReadResults(IEnumerable<string> trxPaths)
    {
        var outcomes = new List<(string, bool)>();
        foreach (var path in trxPaths)
        {
            XDocument doc;
            try { doc = XDocument.Load(path); } catch { continue; }

            var fqnById = BuildFqnIndex(doc);

            foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                var outcome = r.Attribute("outcome")?.Value;
                if (outcome is null) continue;

                var name = r.Attribute("testId")?.Value is { } testId && fqnById.TryGetValue(testId, out var fqn)
                    ? fqn
                    : r.Attribute("testName")?.Value;
                if (name is null) continue;

                outcomes.Add((name, outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)));
            }
        }
        return outcomes;
    }

    private static Dictionary<string, string> BuildFqnIndex(XDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ut in doc.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
        {
            var id = ut.Attribute("id")?.Value;
            var tm = ut.Elements().FirstOrDefault(e => e.Name.LocalName == "TestMethod");
            if (id is null || tm is null) continue;
            var className = tm.Attribute("className")?.Value;
            var name = tm.Attribute("name")?.Value;
            if (className is null || name is null) continue;
            map[id] = $"{className}.{name}";
        }
        return map;
    }

    /// <summary>Resolve the expected fully-qualified test name against TRX rows.
    /// Matches exact names and theory rows (<c>FQN(...)</c>). Passed only if every matching row passed.</summary>
    public static TestOutcome Evaluate(string expectedFqn, List<(string Name, bool Passed)> rows)
    {
        var method = expectedFqn.Contains('.') ? expectedFqn[(expectedFqn.LastIndexOf('.') + 1)..] : expectedFqn;
        var matches = rows
            .Where(r => r.Name == expectedFqn
                        || r.Name.StartsWith(expectedFqn + "(", StringComparison.Ordinal)
                        || r.Name.EndsWith("." + method, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
            return new TestOutcome { Name = expectedFqn, Passed = false, Missing = true };

        return new TestOutcome { Name = expectedFqn, Passed = matches.All(m => m.Passed) };
    }
}
