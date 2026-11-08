using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TaskManager.Engine;

/// <summary>
/// Font families installed for all users and for this user, read from the registry's font lists
/// (entries like "Arial Bold Italic (TrueType)" or "Cambria & Cambria Math (TrueType)") and reduced to family names.
/// </summary>
public static partial class InstalledFonts
{
    private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    [GeneratedRegex(@"\s*\((TrueType|OpenType|All res|VGA res|\d+,\d+.*)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Kind();

    // Style words that follow the family name in an entry; trailing ones are trimmed off one by one.
    private static readonly HashSet<string> s_styles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Regular", "Bold", "Italic", "Oblique", "Light", "Semilight", "SemiLight", "Semibold", "SemiBold", "DemiBold", "Demi",
        "Medium", "Black", "Heavy", "Thin", "ExtraLight", "Extra", "UltraLight", "Ultra", "ExtraBold", "UltraBold", "Condensed",
        "SemiCondensed", "Narrow", "Variable", "Book", "Roman", "BoldItalic", "Display", "Text", "Small",
    };

    public static List<string> Families()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(Key);
                if (k == null) continue;
                foreach (string entry in k.GetValueNames())
                {
                    string name = Kind().Replace(entry, "");
                    if (name.Length == 0 || name.StartsWith('@')) continue;   // '@' = vertical CJK variants
                    foreach (string part in name.Split(" & "))
                    {
                        var words = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                        while (words.Count > 1 && s_styles.Contains(words[^1])) words.RemoveAt(words.Count - 1);
                        if (words.Count > 0) set.Add(string.Join(' ', words));
                    }
                }
            }
            catch (Exception ex) { Log.Error("font list", ex); }
        }
        return [.. set];
    }
}
