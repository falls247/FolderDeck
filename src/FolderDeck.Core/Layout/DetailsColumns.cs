using System.Collections.ObjectModel;

namespace FolderDeck.Core.Layout;

/// <summary>
/// Widths of the resizable columns of the details view (更新日時 / 種類 / サイズ), saved in settings.json
/// under <c>details_columns</c>. The 名前 column has no stored width: it takes what the others leave.
/// </summary>
public static class DetailsColumns
{
    public const string Modified = "modified", Type = "type", Size = "size";

    /// <summary>Bounds of a stored column width (device-independent pixels).</summary>
    public const double Min = 48, Max = 600;

    /// <summary>The column keys in display order (left to right, after 名前).</summary>
    private static readonly string[] Keys = [Modified, Type, Size];

    /// <summary>Default widths: 更新日時 150, 種類 170, サイズ 96.</summary>
    public static IReadOnlyDictionary<string, double> Defaults { get; } =
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [Modified] = 150,
            [Type] = 170,
            [Size] = 96,
        });

    /// <summary>
    /// Returns a new dictionary that holds exactly the three known keys, in display order.
    /// A missing key or a non-finite value becomes the default, any other value is clamped into
    /// <see cref="Min"/>..<see cref="Max"/>, and unknown keys are dropped. Keys are matched ignoring
    /// case and surrounding white space (the file can be edited by hand); the first match wins.
    /// </summary>
    public static Dictionary<string, double> Normalize(IReadOnlyDictionary<string, double>? widths)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string key in Keys)
        {
            result[key] = Defaults[key];
        }

        if (widths is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in widths)
        {
            string? key = KnownKey(pair.Key);
            if (key is null || !seen.Add(key) || !double.IsFinite(pair.Value))
            {
                continue;
            }

            result[key] = Math.Round(Math.Clamp(pair.Value, Min, Max), 2);
        }

        return result;
    }

    /// <summary>
    /// The widths to draw with when the list is <paramref name="available"/> wide: the stored widths
    /// (normalized) when they leave at least <paramref name="minName"/> for the 名前 column, otherwise
    /// widths shrunk in proportion to what each column has above <see cref="Min"/>, rounded down to
    /// whole pixels. No column goes below <see cref="Min"/>, so in a very narrow list the row is wider
    /// than the list and the caller clips it.
    /// The result is for display only and must not be saved: the stored widths come back when the
    /// window grows again. The 名前 column is <c>Math.Max(minName, available - sum of the result)</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Fit(
        double available, IReadOnlyDictionary<string, double> widths, double minName = 120)
    {
        var result = Normalize(widths);
        if (!double.IsFinite(available) || !double.IsFinite(minName))
        {
            return result;
        }

        double budget = available - Math.Max(0, minName);
        double sum = 0;
        double totalSlack = 0;
        foreach (string key in Keys)
        {
            sum += result[key];
            totalSlack += result[key] - Min;
        }

        double excess = sum - budget;
        if (excess <= 0)
        {
            return result;
        }

        foreach (string key in Keys)
        {
            double slack = result[key] - Min;
            result[key] = excess >= totalSlack
                ? Min
                : Math.Max(Min, Math.Floor(result[key] - slack * excess / totalSlack));
        }

        return result;
    }

    private static string? KnownKey(string? key)
    {
        string trimmed = key?.Trim() ?? "";
        foreach (string known in Keys)
        {
            if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }
}
