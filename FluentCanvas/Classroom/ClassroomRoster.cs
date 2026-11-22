using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FluentCanvas.Classroom;

internal sealed record ClassroomRoster(string NamesText, string ReplacementsText, string[] Names, int InvalidRules)
{
    internal static readonly ClassroomRoster Empty = new("", "", Array.Empty<string>(), 0);

    internal static ClassroomRoster Parse(string namesText, string replacementsText)
    {
        var rules = new List<(string Original, string Replacement)>();
        int invalid = 0;
        foreach (string line in Lines(replacementsText))
        {
            int split = line.IndexOf("-->", StringComparison.Ordinal);
            if (split <= 0 || string.IsNullOrWhiteSpace(line[..split]))
            {
                invalid++;
                continue;
            }
            rules.Add((line[..split].Trim(), line[(split + 3)..].Trim()));
        }

        // Later rules may replace an earlier substitution. Resolve them in reverse order.
        var finalNames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = rules.Count - 1; i >= 0; i--)
        {
            var rule = rules[i];
            finalNames[rule.Original] = finalNames.TryGetValue(rule.Replacement, out var result) ? result : rule.Replacement;
        }
        var names = new List<string>();
        foreach (string line in Lines(namesText))
        {
            string name = finalNames.TryGetValue(line, out var result) ? result : line;
            if (name.Length > 0) names.Add(name);
        }
        return new ClassroomRoster(namesText, replacementsText, names.ToArray(), invalid);
    }

    private static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            string value = line.Trim();
            if (value.Length > 0) yield return value;
        }
    }

    internal int Population(int numericPopulation) => Names.Length == 0 ? numericPopulation : Names.Length;

    internal string Label(int index) => Names.Length == 0 ? (index + 1).ToString() : Names[index];

    internal string[] Draw(int numericPopulation, int count, Random random)
    {
        int size = Population(numericPopulation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, size);
        if (count == 1) return [Label(random.Next(size))];
        var indices = Enumerable.Range(0, size).ToArray();
        var results = new string[count];
        for (int i = 0; i < count; i++)
        {
            int other = random.Next(i, size);
            (indices[i], indices[other]) = (indices[other], indices[i]);
            results[i] = Label(indices[i]);
        }
        return results;
    }
}
