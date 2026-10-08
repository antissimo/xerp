namespace Xerp.Application.Common;

/// <summary>Builds SQL <c>LIKE</c> patterns in which user input is always literal text (spec 001, R9, S7).</summary>
public static class LikePattern
{
    public const string EscapeCharacter = "\\";

    /// <summary>A pattern matching rows that contain <paramref name="text"/> as a substring.</summary>
    public static string Contains(string text) =>
        "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
