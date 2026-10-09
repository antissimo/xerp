using System.Runtime.CompilerServices;

namespace Xerp.Application.Common;

/// <summary>
/// Base of inputs that must tell an omitted field from one given as null (spec 002, R11; spec 004, R7, R9):
/// a property whose <c>init</c> goes through <see cref="Given{T}"/> is remembered as given, whatever its value.
/// </summary>
public abstract record TrackedInput
{
    private readonly HashSet<string> _given = new(StringComparer.Ordinal);

    protected T Given<T>(T value, [CallerMemberName] string property = "")
    {
        _given.Add(property);
        return value;
    }

    /// <summary>True when the property was set, also when it was set to null.</summary>
    public bool Has(string property) => _given.Contains(property);
}
