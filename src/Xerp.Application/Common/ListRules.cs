using Xerp.Domain.Common;

namespace Xerp.Application.Common;

/// <summary>The list rules every collection shares (spec 001, R9): search text and paging are rejected, not clamped.</summary>
public static class ListRules
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxSearchLength = 100;

    /// <summary>The trimmed search text, or null when there is no text filter.</summary>
    public static string? Search(ValidationErrors errors, string? input)
    {
        var search = input?.Trim();
        if (string.IsNullOrEmpty(search))
            return null;
        if (search.Length > MaxSearchLength)
            errors.Add("search", $"search must be at most {MaxSearchLength} characters.");
        else if (TextRules.HasControlCharacters(search))
            errors.Add("search", "search must not contain control characters.");
        return search;
    }

    public static int Limit(ValidationErrors errors, int? input)
    {
        var limit = input ?? DefaultLimit;
        if (limit < 1 || limit > MaxLimit)
            errors.Add("limit", $"limit must be between 1 and {MaxLimit}.");
        return limit;
    }

    public static int Offset(ValidationErrors errors, int? input)
    {
        var offset = input ?? 0;
        if (offset < 0)
            errors.Add("offset", "offset must be 0 or greater.");
        return offset;
    }
}
