using Xerp.Application.Common;
using Xerp.Domain.Common;

namespace Xerp.Application.UnitsOfMeasure;

/// <summary>Input rules for units of measure (spec 001, R1-R5, R9). No I/O.</summary>
public static class UnitOfMeasureValidation
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxSearchLength = 100;

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
        var search = input.Search?.Trim();
        if (string.IsNullOrEmpty(search))
            search = null;
        else if (search.Length > MaxSearchLength)
            errors.Add("search", $"search must be at most {MaxSearchLength} characters.");
        else if (TextRules.HasControlCharacters(search))
            errors.Add("search", "search must not contain control characters.");
        var limit = input.Limit ?? DefaultLimit;
        if (limit < 1 || limit > MaxLimit)
            errors.Add("limit", $"limit must be between 1 and {MaxLimit}.");
        var offset = input.Offset ?? 0;
        if (offset < 0)
            errors.Add("offset", "offset must be 0 or greater.");
        if (errors.Any)
            return errors.ToError();
        return new UnitOfMeasureListQuery(search, input.IsActive, limit, offset);
    }
}
