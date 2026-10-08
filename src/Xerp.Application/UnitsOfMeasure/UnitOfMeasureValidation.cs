using Xerp.Application.Common;

namespace Xerp.Application.UnitsOfMeasure;

/// <summary>Input rules for units of measure (spec 001, R1-R5, R9). No I/O.</summary>
public static class UnitOfMeasureValidation
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxSearchLength = 100;

    public static Result<UnitOfMeasureValues> Create(CreateUnitOfMeasureInput input) => throw new NotImplementedException();

    public static Result<UnitOfMeasureValues> Replace(ReplaceUnitOfMeasureInput input) => throw new NotImplementedException();

    public static Result<UnitOfMeasureListQuery> List(ListUnitsOfMeasureInput input) => throw new NotImplementedException();
}
