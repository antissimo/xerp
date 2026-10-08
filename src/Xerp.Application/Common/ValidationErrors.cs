using Xerp.Domain.Common;

namespace Xerp.Application.Common;

/// <summary>Collects field errors (camelCase field name -> messages) into one VALIDATION_FAILED error.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(message);
    }

    /// <summary>Checks a <c>code</c> field (R1-R3); returns the normalised value, or records an error.</summary>
    public string Code(string? input, string field = "code")
    {
        if (CodeRules.TryNormalize(input, out var code))
            return code;
        Add(field, string.IsNullOrWhiteSpace(input)
            ? "Code is required."
            : $"Code must be 1-{CodeRules.MaxLength} characters: letters, digits, '.', '_' or '-'.");
        return "";
    }

    /// <summary>Checks a <c>name</c> field (R4); returns the normalised value, or records an error.</summary>
    public string Name(string? input, string field = "name", int maxLength = NameRules.MaxLength)
    {
        if (NameRules.TryNormalize(input, maxLength, out var name))
            return name;
        Add(field, string.IsNullOrWhiteSpace(input)
            ? "Name is required."
            : $"Name must be at most {maxLength} characters and contain no control characters.");
        return "";
    }

    public AppError ToError() =>
        AppError.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
}
