namespace Xerp.Domain.Common;

/// <summary>Rules shared by every text value a client can send.</summary>
public static class TextRules
{
    /// <summary>
    /// True when the text contains a control character (Unicode category Cc: U+0000-U+001F, U+007F-U+009F),
    /// which includes NUL, tab and line breaks (spec 001, R4 and R9).
    /// </summary>
    public static bool HasControlCharacters(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c))
                return true;
        }
        return false;
    }
}
