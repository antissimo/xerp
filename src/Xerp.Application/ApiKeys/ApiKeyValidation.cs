using Xerp.Application.Common;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.ApiKeys;

/// <summary>Input rules for API keys (spec 003, R1, R2, R10). No I/O.</summary>
public static class ApiKeyValidation
{
    private const string ActorTypeMessage = $"actorType must be \"{ActorTypeNames.Human}\" or \"{ActorTypeNames.Agent}\".";

    public static Result<ApiKeyValues> Create(CreateApiKeyInput input)
    {
        var errors = new ValidationErrors();
        var name = errors.Name(input.Name, maxLength: ApiKey.NameMaxLength);
        if (!ActorTypeNames.TryParse(input.ActorType, out var actorType))
            errors.Add("actorType", string.IsNullOrEmpty(input.ActorType) ? "actorType is required. " + ActorTypeMessage : ActorTypeMessage);
        if (errors.Any)
            return errors.ToError();
        return new ApiKeyValues(name, actorType);
    }

    public static Result<ApiKeyListQuery> List(ListApiKeysInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        ActorType? actorType = null;
        if (!string.IsNullOrEmpty(input.ActorType))
        {
            if (ActorTypeNames.TryParse(input.ActorType, out var parsed)) actorType = parsed;
            else errors.Add("actorType", ActorTypeMessage);
        }
        if (errors.Any)
            return errors.ToError();
        return new ApiKeyListQuery(search, actorType, input.IsActive, limit, offset);
    }
}
