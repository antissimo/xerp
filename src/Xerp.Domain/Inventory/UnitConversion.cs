namespace Xerp.Domain.Inventory;

/// <summary>
/// The one conversion rule (ADR-0014, decisions 2 and 6; spec 007, R2, R14, R15): one of a unit equals
/// <c>factor</c> base units of the article, and a quantity in that unit is <c>quantity × factor</c> base units,
/// rounded to six decimal places, half away from zero. Reading a draft, saving it and posting it all convert here.
/// </summary>
public static class UnitConversion
{
    public const int FactorDecimalPlaces = 6;
    public const decimal MaxFactor = 999_999.999999m;

    /// <summary>The factor of the article's own base unit, which has no conversion (R5).</summary>
    public const decimal BaseUnitFactor = 1m;

    /// <summary>R2: greater than zero, at most <see cref="MaxFactor"/>, at most six decimal places. Never rounded.</summary>
    public static bool IsValidFactor(decimal factor) =>
        factor > 0 && factor <= MaxFactor && decimal.Round(factor, FactorDecimalPlaces) == factor;

    /// <summary>
    /// R14: the quantity in base units. The result may be zero or above the quantity maximum; whether such a
    /// line is acceptable is <see cref="TryToBase"/>.
    /// </summary>
    public static decimal ToBase(decimal quantity, decimal factor) =>
        decimal.Round(quantity * factor, QuantityRules.DecimalPlaces, MidpointRounding.AwayFromZero);

    /// <summary>
    /// R15: false when the quantity converts to zero or to more than a quantity may be - such a line cannot be
    /// saved or posted. <paramref name="baseQuantity"/> is the converted value either way.
    /// </summary>
    public static bool TryToBase(decimal quantity, decimal factor, out decimal baseQuantity)
    {
        baseQuantity = ToBase(quantity, factor);
        return baseQuantity > 0 && baseQuantity <= QuantityRules.Max;
    }
}
