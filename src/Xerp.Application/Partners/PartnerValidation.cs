using Xerp.Application.Common;
using Xerp.Domain.Partners;

namespace Xerp.Application.Partners;

/// <summary>Input rules for partners (spec 004, R1-R9, R13-R15, R18). No I/O.</summary>
public static class PartnerValidation
{
    /// <summary>Roles default to false and <c>isActive</c> to true; optional text may be omitted.</summary>
    public static Result<PartnerValues> Create(PartnerInput input) => Validate(input, replace: false);

    /// <summary>All twelve fields must be present; optional text may be null (R7).</summary>
    public static Result<PartnerValues> Replace(PartnerInput input) => Validate(input, replace: true);

    private static Result<PartnerValues> Validate(PartnerInput input, bool replace)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);

        var rolesBefore = errors.Count;
        var isCustomer = FieldRules.Flag(errors, input, nameof(input.IsCustomer), input.IsCustomer, false, replace);
        var isSupplier = FieldRules.Flag(errors, input, nameof(input.IsSupplier), input.IsSupplier, false, replace);
        // R15 is about the values; when a role field is itself missing or malformed, that is the error to fix first.
        if (errors.Count == rolesBefore && !PartnerRules.HasRole(isCustomer, isSupplier))
        {
            const string message = "At least one of isCustomer and isSupplier must be true: a partner is a customer, a supplier or both.";
            errors.Add("isCustomer", message);
            errors.Add("isSupplier", message);
        }

        var taxId = FieldRules.OptionalText(errors, input, nameof(input.TaxId), input.TaxId, PartnerRules.TaxIdMaxLength, replace);
        var address = FieldRules.AddressOf(errors, input, replace);
        var isActive = FieldRules.Flag(errors, input, nameof(input.IsActive), input.IsActive, true, replace);
        if (errors.Any)
            return errors.ToError();
        return new PartnerValues(code, name, isCustomer, isSupplier, taxId, address, isActive);
    }

    public static Result<PartnerListQuery> List(ListPartnersInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new PartnerListQuery(search, input.IsCustomer, input.IsSupplier, input.IsActive, limit, offset);
    }
}
