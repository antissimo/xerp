using Xerp.Application.ApiKeys;
using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Application.UnitsOfMeasure;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 003, AC-05 (R13), R15 and R7: how a record is addressed is an Application rule. Every case here is
/// decided before any data access, so the operations run without a database.
/// </summary>
public class RecordAddressTests
{
    private sealed record Actor(Guid? TenantId, Guid? ApiKeyId) : ITenantContext;

    private static readonly Actor Me = new(Guid.CreateVersion7(), Guid.CreateVersion7());
    private static readonly UnitOfMeasureOperations Units = new(null!, Me, null!);
    private static readonly ArticleOperations Articles = new(null!, Me, null!);
    private static readonly ApiKeyOperations Keys = new(null!, Me, null!, null!, null!);

    private static void AssertError<T>(Result<T> result, string code, params string[] expectedKeys) where T : notnull
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(expectedKeys.Order(), (result.Error.Errors?.Keys ?? []).Order());
    }

    public static TheoryData<RecordAddressInput> NeitherOrBoth() => new()
    {
        new RecordAddressInput(),
        new RecordAddressInput(Id: null, Code: null),
        new RecordAddressInput(Guid.CreateVersion7().ToString(), "kg"),
        new RecordAddressInput("not-a-uuid", "kg"),
    };

    [Theory]
    [MemberData(nameof(NeitherOrBoth))]
    public async Task AC05_Get_needs_exactly_one_of_id_and_code(RecordAddressInput address)
    {
        AssertError(await Units.FindAsync(address), ErrorCodes.ValidationFailed, "code", "id");
        AssertError(await Articles.FindAsync(address), ErrorCodes.ValidationFailed, "code", "id");
    }

    [Fact]
    public async Task R15_Id_that_is_not_a_uuid_addresses_no_record()
    {
        var unit = new ReplaceUnitOfMeasureInput("kg", "Kilogram", true);
        var article = new ReplaceArticleInput("A1", "Bolt", "stock", Guid.CreateVersion7().ToString(), true) { Description = null };

        AssertError(await Units.FindAsync(new RecordAddressInput(Id: "not-a-uuid")), ErrorCodes.NotFound);
        AssertError(await Units.ReplaceAsync("not-a-uuid", unit), ErrorCodes.NotFound);
        AssertError(await Units.DeleteAsync("not-a-uuid"), ErrorCodes.NotFound);
        AssertError(await Articles.FindAsync(new RecordAddressInput(Id: "not-a-uuid")), ErrorCodes.NotFound);
        AssertError(await Articles.ReplaceAsync("not-a-uuid", article), ErrorCodes.NotFound);
        AssertError(await Articles.DeleteAsync("not-a-uuid"), ErrorCodes.NotFound);
        AssertError(await Keys.GetAsync("not-a-uuid"), ErrorCodes.NotFound);
        AssertError(await Keys.RevokeAsync("not-a-uuid"), ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task R14_Missing_id_is_a_validation_error_under_its_own_name(string? id)
    {
        var unit = new ReplaceUnitOfMeasureInput("kg", "Kilogram", true);

        AssertError(await Units.ReplaceAsync(id, unit), ErrorCodes.ValidationFailed, "id");
        AssertError(await Units.DeleteAsync(id), ErrorCodes.ValidationFailed, "id");
        AssertError(await Articles.DeleteAsync(id), ErrorCodes.ValidationFailed, "id");
        AssertError(await Keys.GetAsync(id), ErrorCodes.ValidationFailed, "id");
        AssertError(await Keys.RevokeAsync(id), ErrorCodes.ValidationFailed, "id");
    }

    [Fact]
    public async Task R7_Key_cannot_revoke_itself()
    {
        AssertError(await Keys.RevokeAsync(Me.ApiKeyId!.Value), ErrorCodes.CannotRevokeSelf);
        AssertError(await Keys.RevokeAsync(Me.ApiKeyId.Value.ToString()), ErrorCodes.CannotRevokeSelf);
    }
}
