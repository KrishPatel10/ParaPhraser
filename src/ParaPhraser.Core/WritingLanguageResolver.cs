using System.Text.RegularExpressions;

namespace ParaPhraser.Core;

public static class WritingLanguageResolver
{
    private static readonly HashSet<string> HinglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "aaj", "aap", "abhi", "acha", "accha", "aur", "bahut", "bhej", "bhejna",
        "chahiye", "dena", "dunga", "gaya", "hai", "hain", "ham", "ho", "hoga",
        "hogi", "hum", "ka", "kal", "kar", "karna", "karo", "ke", "ki", "kiya",
        "ko", "kyunki", "lekin", "main", "mein", "mera", "meri", "mujhe", "nahi",
        "nahin", "par", "phir", "raha", "rahi", "sakta", "sakti", "se", "thoda",
        "tum", "wala", "wali"
    };

    public static WritingLanguage Resolve(string text, WritingLanguage preference)
    {
        if (preference != WritingLanguage.Auto)
        {
            return preference;
        }

        if (text.Any(character => character is >= '\u0900' and <= '\u097F'))
        {
            return WritingLanguage.Hindi;
        }

        var matches = Regex.Matches(text, "[A-Za-z]+");
        var hinglishWordCount = matches
            .Select(match => match.Value)
            .Count(HinglishWords.Contains);

        return hinglishWordCount >= 2
            ? WritingLanguage.Hinglish
            : WritingLanguage.English;
    }
}
