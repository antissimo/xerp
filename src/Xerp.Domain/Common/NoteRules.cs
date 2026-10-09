namespace Xerp.Domain.Common;

/// <summary>
/// Rule for optional multi-line text such as a note (spec 005, R7; as an article description, spec 002, R3):
/// trimmed; empty means "no value" (null); otherwise at most the field's maximum length and no control
/// characters other than line feed, carriage return and tab.
/// </summary>
public static class NoteRules
{
    public static bool TryNormalize(string? input, int maxLength, out string? value)
    {
        var trimmed = input?.Trim();
        value = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        return value is null || (value.Length <= maxLength && !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')));
    }
}
