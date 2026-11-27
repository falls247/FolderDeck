using System.Globalization;

namespace FolderDeck.Core.Ordering;

/// <summary>
/// The search of the shown list (Ctrl+F): which items' names hold what was typed. The words of
/// the query (split at spaces, also the full-width one) must all appear in the name, anywhere and
/// in any order; case, full / half width and hiragana / katakana are ignored, so "ｒｅａｄｍｅ",
/// "README" and "readme" find the same file, and "かいぎ" finds "カイギ". Only names are searched
/// (not the path, not the contents); the list is not walked into subfolders.
/// </summary>
public static class NameSearch
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

    private static readonly char[] Separators = [' ', '　', '\t'];

    /// <summary>The words of <paramref name="query"/>; empty when there is nothing to search for.</summary>
    public static string[] Terms(string? query) =>
        string.IsNullOrWhiteSpace(query) ? [] : query.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every one of <paramref name="terms"/> appears in <paramref name="name"/> (no terms: true).</summary>
    public static bool Matches(string name, IReadOnlyList<string> terms)
    {
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        foreach (string term in terms)
        {
            if (compare.IndexOf(name, term, Options) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The items whose name matches <paramref name="query"/>, in order (<paramref name="items"/> itself for an empty query).</summary>
    public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, string? query, Func<T, string> name)
    {
        var terms = Terms(query);
        if (terms.Length == 0)
        {
            return items;
        }

        var found = new List<T>();
        foreach (var item in items)
        {
            if (Matches(name(item), terms))
            {
                found.Add(item);
            }
        }

        return found;
    }
}
