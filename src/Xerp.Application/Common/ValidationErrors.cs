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

    public AppError ToError() =>
        AppError.Validation(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
}
