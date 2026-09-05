using System.Globalization;
using System.Windows.Media;

namespace MemeMomo.UI.Text;

internal static class FontFallbackResolver
{
    internal static string ResolveFamily(
        string text,
        params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (SupportsText(candidate, text))
            {
                return candidate;
            }
        }

        return "Global User Interface";
    }

    internal static bool SupportsText(string familyName, string text)
    {
        System.Windows.Media.FontFamily family = new(familyName);
        Typeface typeface = new(
            family,
            System.Windows.FontStyles.Normal,
            System.Windows.FontWeights.Normal,
            System.Windows.FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out GlyphTypeface? glyphTypeface))
        {
            return false;
        }

        return StringInfo.GetTextElementEnumerator(text)
            .AsEnumerable()
            .Select(element => char.ConvertToUtf32(element, 0))
            .All(codePoint => glyphTypeface.CharacterToGlyphMap.ContainsKey(codePoint));
    }

    private static IEnumerable<string> AsEnumerable(this TextElementEnumerator enumerator)
    {
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }
}
