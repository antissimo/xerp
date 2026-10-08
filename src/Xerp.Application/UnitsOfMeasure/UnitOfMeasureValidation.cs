using Xerp.Application.Common;
using Xerp.Domain.Common;

namespace Xerp.Application.UnitsOfMeasure;

/// <summary>Input rules for units of measure (spec 001, R1-R5, R9). No I/O.</summary>
public static class UnitOfMeasureValidation
{
    public const int DefaultLimit = ListRules.DefaultLimit;
    public const int MaxLimit = ListRules.MaxLimit;
    public const int MaxSearchLength = ListRules.MaxSearchLength;

    public static Result<UnitOfMeasureValues> Create(CreateUnitOfMeasureInput input)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        if (errors.Any)
            return errors.ToError();
        return new UnitOfMeasureValues(code, name, input.IsActive ?? true);
    }

    public static Result<UnitOfMeasureValues> Replace(ReplaceUnitOfMeasureInput input)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        if (input.IsActive is null)
            errors.Add("isActive", "isActive is required.");
        if (errors.Any)
            return errors.ToError();
        return new UnitOfMeasureValues(code, name, input.IsActive!.Value);
    }

    public static Result<UnitOfMeasureListQuery> List(ListUnitsOfMeasureInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new UnitOfMeasureListQuery(search, input.IsActive, limit, offset);
    }
}
