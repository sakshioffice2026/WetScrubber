using System.Text.RegularExpressions;

namespace EngineeringAI.Core.Security;

public static class PromptSanitizer
{
    private const int MaxLength = 2000;

    private static readonly Regex ControlTokens = new(
        @"<\|[^|>]*\|>|\[/?INST\]|<<SYS>>|<</SYS>>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InjectionPhrases = new(
        @"ignore\s+(all\s+)?(previous|prior|above)\s+(instructions|rules|prompts?)|" +
        @"disregard\s+(all\s+)?(previous|prior|above)|" +
        @"forget\s+(all\s+)?(previous|prior|above)|" +
        @"you\s+are\s+now\s+|" +
        @"act\s+as\s+(an?\s+)?(different|new)|" +
        @"reveal\s+(your\s+)?(system\s+)?prompt|" +
        @"print\s+(your\s+)?(system\s+)?prompt|" +
        @"developer\s+mode|jailbreak",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ControlChars = new(
        @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]",
        RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s{3,}", RegexOptions.Compiled);

    public static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var text = input.Trim();

        if (text.Length > MaxLength)
        {
            text = text[..MaxLength];
        }

        text = ControlChars.Replace(text, string.Empty);
        text = ControlTokens.Replace(text, string.Empty);
        text = InjectionPhrases.Replace(text, "[removed]");
        text = Whitespace.Replace(text, "  ");

        return text.Trim();
    }

    public static bool LooksLikeInjection(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return ControlTokens.IsMatch(input) || InjectionPhrases.IsMatch(input);
    }
}