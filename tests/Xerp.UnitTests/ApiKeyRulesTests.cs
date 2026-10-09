using Xerp.Application.ApiKeys;
using Xerp.Application.Common;
using Xerp.Domain.Tenancy;

namespace Xerp.UnitTests;

/// <summary>Spec 003, R1, R2, R5, R6, R10: API key rules in Domain and Application, without HTTP or MCP.</summary>
public class ApiKeyRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('a', 64);

    private static void AssertInvalid<T>(Result<T> result, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal(expectedKeys.Order(), result.Error.Errors!.Keys.Order());
    }

    [Fact]
    public void R3_Created_key_is_active_and_records_its_creator()
    {
        var creator = Guid.CreateVersion7();

        var key = ApiKey.Create(Guid.CreateVersion7(), " bot ", ActorType.Agent, Hash, T0, creator);

        Assert.Equal("bot", key.Name);
        Assert.True(key.IsActive);
        Assert.Equal(creator, key.CreatedBy);
        Assert.Null(key.RevokedAt);
        Assert.Null(key.RevokedBy);
        Assert.Null(ApiKey.Create(Guid.CreateVersion7(), "initial", ActorType.Human, Hash, T0).CreatedBy);
    }

    [Fact]
    public void R5_R6_Revoke_is_permanent_and_a_second_revoke_keeps_the_first_values()
    {
        var first = Guid.CreateVersion7();
        var key = ApiKey.Create(Guid.CreateVersion7(), "bot", ActorType.Agent, Hash, T0);

        key.Revoke(T0.AddMinutes(1), first);
        key.Revoke(T0.AddMinutes(2), Guid.CreateVersion7());

        Assert.False(key.IsActive);
        Assert.Equal(T0.AddMinutes(1), key.RevokedAt);
        Assert.Equal(first, key.RevokedBy);
    }

    [Theory]
    [InlineData("human", true)]
    [InlineData("agent", true)]
    [InlineData("Agent", false)]
    [InlineData("robot", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void R2_Actor_type_is_exactly_human_or_agent(string? name, bool valid)
    {
        Assert.Equal(valid, ActorTypeNames.TryParse(name, out _));
    }

    [Fact]
    public void R1_R2_Create_trims_the_name_and_parses_the_actor_type()
    {
        var result = ApiKeyValidation.Create(new CreateApiKeyInput("  bot  ", "agent"));

        Assert.True(result.IsSuccess);
        Assert.Equal(new ApiKeyValues("bot", ActorType.Agent), result.Value);
        Assert.True(ApiKeyValidation.Create(new CreateApiKeyInput(new string('n', 100), "human")).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    public void R1_Invalid_name_is_rejected(string? name)
    {
        AssertInvalid(ApiKeyValidation.Create(new CreateApiKeyInput(name, "agent")), "name");
    }

    [Fact]
    public void R1_R2_All_invalid_fields_are_reported_together()
    {
        AssertInvalid(ApiKeyValidation.Create(new CreateApiKeyInput(new string('n', 101), "agent")), "name");
        AssertInvalid(ApiKeyValidation.Create(new CreateApiKeyInput("bot", null)), "actorType");
        AssertInvalid(ApiKeyValidation.Create(new CreateApiKeyInput(null, "Agent")), "actorType", "name");
    }

    [Fact]
    public void R10_List_applies_the_shared_list_rules_and_parses_the_actor_type()
    {
        var query = ApiKeyValidation.List(new ListApiKeysInput(Search: "  bot ", ActorType: "agent"));

        Assert.True(query.IsSuccess);
        Assert.Equal(new ApiKeyListQuery("bot", ActorType.Agent, null, 50, 0), query.Value);
        Assert.Null(ApiKeyValidation.List(new ListApiKeysInput(ActorType: "")).Value!.ActorType);
        AssertInvalid(ApiKeyValidation.List(new ListApiKeysInput(ActorType: "robot", Limit: 0, Offset: -1)), "actorType", "limit", "offset");
    }
}
