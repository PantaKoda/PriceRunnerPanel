using PriceRunnerPanel.Models;

namespace PriceRunnerPanel.Services;

/// <summary>One box on the watchlist: a product and the variants of it that are pinned.</summary>
public sealed record PinCluster(string Title, IReadOnlyList<(PinnedProduct Pin, string? Variant)> Members);

/// <summary>
/// Groups pinned products that are the same product in different variants (usually colours). PriceRunner's own
/// variant groups come first; a pin without one (often a single store's listing, such as
/// "Sony WH-1000XM6 Trådlösa Hörlurar Sand Pink") joins a group whose base name its own name starts with, in the
/// same category. Everything else stands alone.
/// </summary>
public static class ProductGrouping
{
    private static readonly char[] TrimChars = [' ', '-', '–', '—', ',', '/', '(', ')', ' '];

    public static IReadOnlyList<PinCluster> Cluster(IReadOnlyList<PinnedProduct> pins)
    {
        var clusters = new List<(string Key, string Title, string? Category, List<(PinnedProduct Pin, string? Variant)> Members, int First)>();

        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.GroupKey is not { } key)
            {
                continue;
            }

            var index = clusters.FindIndex(c => c.Key == key);
            if (index < 0)
            {
                clusters.Add((key, pin.GroupName ?? pin.Name, pin.Category, [], i));
                index = clusters.Count - 1;
            }

            clusters[index].Members.Add((pin, pin.Variant));
        }

        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin.GroupKey is not null)
            {
                continue;
            }

            var index = clusters.FindIndex(c => c.Category == pin.Category && StartsWithWords(pin.Name, c.Title));
            if (index >= 0)
            {
                var rest = Trim(pin.Name[clusters[index].Title.Length..]);
                clusters[index].Members.Add((pin, rest.Length > 0 ? rest : null));
                clusters[index] = clusters[index] with { First = Math.Min(clusters[index].First, i) };
            }
            else
            {
                clusters.Add(("id:" + pin.Id, pin.Name, pin.Category, [(pin, null)], i));
            }
        }

        // Keep the watchlist order: a group sits where its first pinned member is; members keep pinning order.
        return [.. clusters
            .OrderBy(c => c.First)
            .Select(c => new PinCluster(c.Title, [.. c.Members.OrderBy(m => IndexOf(pins, m.Pin))]))];
    }

    /// <summary>Trims separators left over after removing a variant from a name ("Sony WH-1000XM6 - " → "Sony WH-1000XM6").</summary>
    public static string Trim(string text) => text.Trim(TrimChars);

    /// <summary>The words all names start with.</summary>
    public static string CommonPrefix(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return "";
        }

        var words = names[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = words.Length;
        foreach (var name in names.Skip(1))
        {
            var other = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shared = 0;
            while (shared < Math.Min(count, other.Length) && string.Equals(words[shared], other[shared], StringComparison.OrdinalIgnoreCase))
            {
                shared++;
            }

            count = shared;
        }

        return Trim(string.Join(' ', words.Take(count)));
    }

    /// <summary>"Sony WH-1000XM6 Sand Pink" starts with "Sony WH-1000XM6"; "Sony WH-1000XM60" does not.</summary>
    private static bool StartsWithWords(string name, string prefix) =>
        prefix.Length > 0
        && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (name.Length == prefix.Length || !char.IsLetterOrDigit(name[prefix.Length]));

    private static int IndexOf(IReadOnlyList<PinnedProduct> pins, PinnedProduct pin)
    {
        for (var i = 0; i < pins.Count; i++)
        {
            if (ReferenceEquals(pins[i], pin))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
