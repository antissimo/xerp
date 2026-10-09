using Xerp.Domain.Common;

namespace Xerp.Domain.Catalog;

public enum ArticleType
{
    /// <summary>A physical item whose quantity is tracked in the stock ledger.</summary>
    Stock,

    /// <summary>Never stocked.</summary>
    Service,
}

/// <summary>The contract names of <see cref="ArticleType"/> (<c>stock</c>, <c>service</c>), as stored and as sent to clients.</summary>
public static class ArticleTypeNames
{
    public const string Stock = "stock";
    public const string Service = "service";

    public static string ToName(this ArticleType type) => type switch
    {
        ArticleType.Stock => Stock,
        ArticleType.Service => Service,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ArticleType Parse(string name) =>
        TryParse(name, out var type) ? type : throw new ArgumentException($"Unknown article type '{name}'.", nameof(name));

    /// <summary>Exact, case-sensitive match (spec 002, R4).</summary>
    public static bool TryParse(string? name, out ArticleType type)
    {
        switch (name)
        {
            case Stock:
                type = ArticleType.Stock;
                return true;
            case Service:
                type = ArticleType.Service;
                return true;
            default:
                type = default;
                return false;
        }
    }
}

/// <summary>Field rules of an article beyond the shared code and name rules (spec 002, R3).</summary>
public static class ArticleRules
{
    public const int DescriptionMaxLength = 2000;

    /// <summary>
    /// Trims the input; empty becomes null. False when the result is longer than the maximum or contains
    /// a control character other than line feed, carriage return or tab (R3).
    /// </summary>
    public static bool TryNormalizeDescription(string? input, out string? description)
    {
        var trimmed = input?.Trim();
        description = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        return description is null || (description.Length <= DescriptionMaxLength && !HasForbiddenControlCharacters(description));
    }

    private static bool HasForbiddenControlCharacters(string text)
    {
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                return true;
        }
        return false;
    }

    /// <summary>The normalised description, or an <see cref="ArgumentException"/> when it is too long.</summary>
    public static string? NormalizeDescription(string? input, string paramName) =>
        TryNormalizeDescription(input, out var description) ? description : throw new ArgumentException("Invalid description.", paramName);
}

/// <summary>An item the company stocks, buys or sells, or a service it sells.</summary>
public sealed class Article : ITenantOwned
{
    private Article() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public ArticleType Type { get; private set; }

    /// <summary>The base unit of measure; always a unit of the same tenant.</summary>
    public Guid BaseUnitId { get; private set; }

    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static Article Create(
        string code, string name, string? description, ArticleType type, Guid baseUnitId, bool isActive,
        DateTime now, Guid actorKeyId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Code = CodeRules.Normalize(code, nameof(code)),
            Name = NameRules.Normalize(name, nameof(name)),
            Description = ArticleRules.NormalizeDescription(description, nameof(description)),
            Type = type,
            BaseUnitId = baseUnitId,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actorKeyId,
            UpdatedBy = actorKeyId,
        };

    public void Replace(
        string code, string name, string? description, ArticleType type, Guid baseUnitId, bool isActive,
        DateTime now, Guid actorKeyId)
    {
        // Validate everything before assigning anything, so a rejected replace leaves the article untouched.
        var newCode = CodeRules.Normalize(code, nameof(code));
        var newName = NameRules.Normalize(name, nameof(name));
        var newDescription = ArticleRules.NormalizeDescription(description, nameof(description));
        Code = newCode;
        Name = newName;
        Description = newDescription;
        Type = type;
        BaseUnitId = baseUnitId;
        IsActive = isActive;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}
