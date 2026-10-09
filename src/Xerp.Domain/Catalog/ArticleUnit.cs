using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Domain.Catalog;

/// <summary>
/// A unit conversion of one article (ADR-0014): one <see cref="UnitId"/> of this article equals
/// <see cref="Factor"/> of the article's base unit - <c>1 box = 12 pcs</c>. It belongs to the article and has
/// no id of its own; the same unit may have another factor on another article. The base unit itself never has
/// a conversion: its factor is 1 by definition.
/// </summary>
public sealed class ArticleUnit : ITenantOwned
{
    private ArticleUnit() { }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public Guid ArticleId { get; private set; }
    public Guid UnitId { get; private set; }

    /// <summary>How many base units one <see cref="UnitId"/> is (spec 007, R2).</summary>
    public decimal Factor { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    /// <param name="baseUnitId">The base unit of the article, which cannot be given a conversion (R5).</param>
    /// <exception cref="ArgumentException">The unit is the base unit, or the factor breaks its rule.</exception>
    public static ArticleUnit Create(Guid articleId, Guid baseUnitId, Guid unitId, decimal factor, DateTime now, Guid actorKeyId)
    {
        if (unitId == baseUnitId)
            throw new ArgumentException("The base unit of an article has no conversion; its factor is 1.", nameof(unitId));
        var conversion = new ArticleUnit { ArticleId = articleId, UnitId = unitId, CreatedAt = now, CreatedBy = actorKeyId };
        conversion.SetFactor(factor, now, actorKeyId);
        return conversion;
    }

    /// <summary>
    /// Replaces the factor (R7): allowed at any time. Nothing stored anywhere else changes with it - posted
    /// lines carry their own factor and the ledger holds base quantities (R17, S2).
    /// </summary>
    /// <exception cref="ArgumentException">The factor breaks its rule.</exception>
    public void SetFactor(decimal factor, DateTime now, Guid actorKeyId)
    {
        if (!UnitConversion.IsValidFactor(factor))
            throw new ArgumentException("A conversion factor is greater than 0, at most 999999.999999, with at most 6 decimal places.", nameof(factor));
        Factor = factor;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}
