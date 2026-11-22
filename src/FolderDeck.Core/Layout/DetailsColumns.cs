using System.Collections.ObjectModel;

namespace FolderDeck.Core.Layout;

/// <summary>
/// Widths of the columns of the details view (名前 / 更新日時 / 種類 / サイズ), saved in settings.json
/// under <c>details_columns</c>. Every column has its own stored width, as in Explorer: the space
/// right of the last column stays empty.
/// </summary>
public static class DetailsColumns
{
    public const string Name = "name", Modified = "modified", Type = "type", Size = "size";

    /// <summary>Bounds of a stored width of 更新日時 / 種類 / サイズ (device-independent pixels).</summary>
    public const double Min = 48, Max = 600;

    /// <summary>Bounds of the stored width of 名前 (device-independent pixels).</summary>
    public const double NameMin = 120, NameMax = 2000;

    /// <summary>The column keys in display order (left to right); also the order of the normalized dictionary.</summary>
    private static readonly string[] Keys = [Name, Modified, Type, Size];

    /// <summary>The columns <see cref="Fit"/> shrinks in proportion once 名前 is down to its minimum.</summary>
    private static readonly string[] Resizable = [Modified, Type, Size];

    /// <summary>Default widths: 名前 360, 更新日時 150, 種類 170, サイズ 96.</summary>
    public static IReadOnlyDictionary<string, double> Defaults { get; } =
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [Name] = 360,
            [Modified] = 150,
            [Type] = 170,
            [Size] = 96,
        });

    /// <summary>The smallest stored width of the column <paramref name="key"/>.</summary>
    public static double MinOf(string key) => key == Name ? NameMin : Min;

    /// <summary>The largest stored width of the column <paramref name="key"/>.</summary>
    public static double MaxOf(string key) => key == Name ? NameMax : Max;

    /// <summary>
    /// Returns a new dictionary that holds exactly the four known keys, in display order.
    /// A missing key or a non-finite value becomes the default (so a file written before 名前 had a
    /// width loads with the default), any other value is clamped into <see cref="MinOf"/>..<see cref="MaxOf"/>,
    /// and unknown keys are dropped. Keys are matched ignoring case and surrounding white space (the
    /// file can be edited by hand); the first match wins.
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

            result[key] = Math.Round(Math.Clamp(pair.Value, MinOf(key), MaxOf(key)), 2);
        }

        return result;
    }

    /// <summary>
    /// The widths to draw with when the list is <paramref name="available"/> wide. When the stored
    /// widths (normalized) fit, they are returned as they are and the rest of the list stays empty to
    /// the right of the last column. Otherwise 名前 gives way first, down to <paramref name="minName"/>
    /// (never wider than stored); if that is not enough, 更新日時 / 種類 / サイズ shrink in proportion
    /// to what each has above <see cref="Min"/>, rounded down to whole pixels. No column goes below
    /// <see cref="Min"/>, so in a very narrow list the row is wider than the list and the caller clips it.
    /// The result is for display only and must not be saved: the stored widths come back when the
    /// window grows again.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Fit(
        double available, IReadOnlyDictionary<string, double> widths, double minName = NameMin)
    {
        var result = Normalize(widths);
        if (!double.IsFinite(available) || !double.IsFinite(minName))
        {
            return result;
        }

        // 名前 shrinks to minName before the others give anything (it never grows here).
        double nameFloor = Math.Min(result[Name], Math.Max(0, minName));
        double budget = available - nameFloor;
        double sum = 0;
        double totalSlack = 0;
        foreach (string key in Resizable)
        {
            sum += result[key];
            totalSlack += result[key] - Min;
        }

        double excess = sum - budget;
        if (excess > 0)
        {
            sum = 0;
            foreach (string key in Resizable)
            {
                double slack = result[key] - Min;
                result[key] = excess >= totalSlack
                    ? Min
                    : Math.Max(Min, Math.Floor(result[key] - slack * excess / totalSlack));
                sum += result[key];
            }
        }

        double room = available - sum;
        if (room < result[Name])
        {
            result[Name] = Math.Max(nameFloor, Math.Floor(room));
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
