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

    public static string ToName(this ArticleType type) => throw new NotImplementedException();

    /// <summary>Exact, case-sensitive match (spec 002, R4).</summary>
    public static bool TryParse(string? name, out ArticleType type) => throw new NotImplementedException();
}

/// <summary>Field rules of an article beyond the shared code and name rules (spec 002, R3).</summary>
public static class ArticleRules
{
    public const int DescriptionMaxLength = 2000;

    /// <summary>Trims the input; empty becomes null. False when the result is longer than the maximum.</summary>
    public static bool TryNormalizeDescription(string? input, out string? description) => throw new NotImplementedException();
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
        throw new NotImplementedException();

    public void Replace(
        string code, string name, string? description, ArticleType type, Guid baseUnitId, bool isActive,
        DateTime now, Guid actorKeyId) =>
        throw new NotImplementedException();
}
