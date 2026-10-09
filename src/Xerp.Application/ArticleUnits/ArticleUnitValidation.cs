using System.Globalization;
using Xerp.Application.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Application.ArticleUnits;

/// <summary>Input rules for article units (spec 007, R2). No I/O.</summary>
public static class ArticleUnitValidation
{
    public const string FactorField = "factor";

    /// <summary>R2: required, greater than 0, at most 999999.999999, at most 6 decimal places; rejected, never rounded.</summary>
    public static Result<FactorValue> Set(SetArticleUnitInput input)
    {
        if (input.Factor is not { } factor)
            return AppError.Validation(FactorField, "factor is required: how many base units of the article one of this unit is, as a JSON number.");
        if (!UnitConversion.IsValidFactor(factor))
            return AppError.Validation(FactorField,
                $"factor must be greater than 0, at most {UnitConversion.MaxFactor.ToString(CultureInfo.InvariantCulture)}, "
                + $"with at most {UnitConversion.FactorDecimalPlaces} decimal places.");
        return new FactorValue(factor);
    }

    public static Result<ArticleUnitListQuery> List(ListArticleUnitsInput input)
    {
        var errors = new ValidationErrors();
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new ArticleUnitListQuery(limit, offset);
    }
}

/// <summary>A factor that obeys R2.</summary>
public sealed record FactorValue(decimal Factor);
