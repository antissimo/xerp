using Xerp.Application.Common;
using Xerp.Domain.Catalog;

namespace Xerp.Application.Articles;

/// <summary>Input rules for articles (spec 002, R1-R5, R10, R11, R18, R19). No I/O.</summary>
public static class ArticleValidation
{
    public static Result<ArticleValues> Create(CreateArticleInput input)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        var description = Description(errors, input.Description);
        var type = RequiredType(errors, input.Type);
        var baseUnitId = RequiredBaseUnitId(errors, input.BaseUnitId);
        if (errors.Any)
            return errors.ToError();
        return new ArticleValues(code, name, description, type, baseUnitId, input.IsActive ?? true);
    }

    public static Result<ArticleValues> Replace(ReplaceArticleInput input)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        if (!input.DescriptionProvided)
            errors.Add("description", "description is required (it may be null).");
        var description = Description(errors, input.Description);
        var type = RequiredType(errors, input.Type);
        var baseUnitId = RequiredBaseUnitId(errors, input.BaseUnitId);
        if (input.IsActive is null)
            errors.Add("isActive", "isActive is required.");
        if (errors.Any)
            return errors.ToError();
        return new ArticleValues(code, name, description, type, baseUnitId, input.IsActive!.Value);
    }

    public static Result<ArticleListQuery> List(ListArticlesInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);

        ArticleType? type = null;
        if (!string.IsNullOrEmpty(input.Type))
        {
            if (ArticleTypeNames.TryParse(input.Type, out var parsed)) type = parsed;
            else errors.Add("type", TypeMessage);
        }
        Guid? baseUnitId = null;
        if (!string.IsNullOrEmpty(input.BaseUnitId))
        {
            if (Guid.TryParse(input.BaseUnitId, out var parsed)) baseUnitId = parsed;
            else errors.Add("baseUnitId", "baseUnitId must be a UUID.");
        }

        if (errors.Any)
            return errors.ToError();
        return new ArticleListQuery(search, type, baseUnitId, input.IsActive, limit, offset);
    }

    private const string TypeMessage = $"type must be \"{ArticleTypeNames.Stock}\" or \"{ArticleTypeNames.Service}\".";

    private static string? Description(ValidationErrors errors, string? input)
    {
        if (!ArticleRules.TryNormalizeDescription(input, out var description))
            errors.Add("description", $"description must be at most {ArticleRules.DescriptionMaxLength} characters and contain no control characters other than line breaks and tabs.");
        return description;
    }

    private static ArticleType RequiredType(ValidationErrors errors, string? input)
    {
        if (!ArticleTypeNames.TryParse(input, out var type))
            errors.Add("type", string.IsNullOrEmpty(input) ? "type is required. " + TypeMessage : TypeMessage);
        return type;
    }

    /// <summary>Only the form is checked here; whether the unit exists is a later stage (R13).</summary>
    private static Guid RequiredBaseUnitId(ValidationErrors errors, string? input)
    {
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add("baseUnitId", string.IsNullOrEmpty(input)
            ? "baseUnitId is required."
            : "baseUnitId must be the id (UUID) of a unit of measure, not its code.");
        return default;
    }
}
