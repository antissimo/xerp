using System.Globalization;
using Xerp.Application.Common;

namespace Xerp.Api.Http;

/// <summary>
/// Turns query-string values into typed input. Only "is this an integer / a boolean / given once" is
/// decided here; ranges, lengths and allowed values are Application rules. An empty value is the same
/// as an absent one.
/// </summary>
public sealed class QueryBinder(IQueryCollection query)
{
    private readonly ValidationErrors _errors = new();

    /// <summary>The binding errors collected so far, or null when every value read could be bound.</summary>
    public AppError? Error => _errors.Any ? _errors.ToError() : null;

    public string? Text(string name)
    {
        var values = query[name];
        if (values.Count > 1)
            _errors.Add(name, $"{name} may be given only once.");
        var value = values.ToString();
        return value.Length == 0 ? null : value;
    }

    public int? Int(string name)
    {
        if (Text(name) is not { } text)
            return null;
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            return value;
        _errors.Add(name, $"{name} must be an integer.");
        return null;
    }

    public bool? Bool(string name)
    {
        switch (Text(name))
        {
            case null: return null;
            case "true": return true;
            case "false": return false;
            default:
                _errors.Add(name, $"{name} must be true or false.");
                return null;
        }
    }
}
