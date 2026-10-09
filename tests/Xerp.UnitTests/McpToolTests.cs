using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Xerp.Api.Mcp;
using Xerp.Application.Common;
using Xerp.Application.UnitsOfMeasure;

namespace Xerp.UnitTests;

/// <summary>Spec 003, AC-04 (result mapping), R14 (argument binding) and the tool catalogue's consistency.</summary>
public class McpToolTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement OnlyText(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement.Clone();

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void AC04_AppError_becomes_a_tool_error_with_the_same_code_detail_and_errors()
    {
        var error = new AppError("CODE_TAKEN", "The code is taken.", new Dictionary<string, string[]> { ["code"] = ["taken", "really"] });

        var result = ToolResults.Error(error);

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        var body = OnlyText(result);
        Assert.Equal(["code", "detail", "errors"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("CODE_TAKEN", body.GetProperty("code").GetString());
        Assert.Equal("The code is taken.", body.GetProperty("detail").GetString());
        Assert.Equal(["taken", "really"], body.GetProperty("errors").GetProperty("code").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public void AC04_AppError_without_field_errors_has_no_errors_property()
    {
        var body = OnlyText(ToolResults.Error(AppError.NotFound("No such unit.")));

        Assert.Equal(["code", "detail"], body.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public void AC04_Arbitrary_exception_becomes_INTERNAL_ERROR_without_its_message_or_type()
    {
        var result = ToolResults.Unexpected(new InvalidTimeZoneException("secret-connection-detail"));

        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal("INTERNAL_ERROR", OnlyText(result).GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(OnlyText(result).GetProperty("detail").GetString()));
        Assert.DoesNotContain("secret-connection-detail", text);
        Assert.DoesNotContain("InvalidTimeZone", text);
    }

    [Fact]
    public void Success_carries_the_object_as_structured_content_and_as_one_text_block()
    {
        var result = ToolResults.Success(new UnitOfMeasureDeleted(), Web);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("""{"deleted":true}""", result.StructuredContent!.Value.GetRawText());
        Assert.Equal("""{"deleted":true}""", OnlyText(result).GetRawText());
    }

    [Fact]
    public void R14_Arguments_bind_like_a_request_body()
    {
        var bound = ToolArguments.Bind<ListUnitsOfMeasureInput>(Args("""{"search":"kg","isActive":true,"limit":5}"""));
        var none = ToolArguments.Bind<ListUnitsOfMeasureInput>(null);

        Assert.Equal(new ListUnitsOfMeasureInput("kg", true, 5, null), bound.Value);
        Assert.Equal(new ListUnitsOfMeasureInput(), none.Value);
    }

    [Theory]
    [InlineData("""{"limit":"10"}""", "limit")]
    [InlineData("""{"isActive":"true"}""", "isActive")]
    [InlineData("""{"foo":1}""", "foo")]
    [InlineData("""{"tenantId":"0198c0de-0000-7000-8000-000000000001"}""", "tenantId")]
    [InlineData("""{"Limit":1}""", "Limit")]
    public void R14_Unknown_argument_or_wrong_json_type_is_a_validation_error_under_its_name(string json, string key)
    {
        var result = ToolArguments.Bind<ListUnitsOfMeasureInput>(Args(json));

        Assert.Equal(ErrorCodes.ValidationFailed, result.Error!.Code);
        Assert.Equal([key], result.Error.Errors!.Keys);
    }

    [Fact]
    public void R14_Id_argument_is_separated_from_the_fields_of_the_record()
    {
        var bound = ToolArguments.BindWithId<ReplaceUnitOfMeasureInput>(Args("""{"id":"abc","code":"kg","name":"Kilogram","isActive":true}"""));
        var noId = ToolArguments.BindWithId<ReplaceUnitOfMeasureInput>(Args("""{"code":"kg","name":"Kilogram","isActive":true}"""));
        var wrongType = ToolArguments.BindWithId<ReplaceUnitOfMeasureInput>(Args("""{"id":5,"code":"kg","name":"Kilogram","isActive":true}"""));

        Assert.Equal(("abc", new ReplaceUnitOfMeasureInput("kg", "Kilogram", true)), bound.Value);
        Assert.Null(noId.Value.Id);
        Assert.Equal(["id"], wrongType.Error!.Errors!.Keys);
    }

    [Fact]
    public void Catalogue_input_schemas_are_closed_and_list_exactly_the_bound_properties()
    {
        Assert.Equal(45, ToolCatalog.All.Count); // spec 004 adds ten, spec 005 eight, spec 006 one, spec 007 four, spec 009 eight
        foreach (var tool in ToolCatalog.All)
        {
            var schema = tool.InputSchema;
            var declared = schema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(p => p.Name).Order().ToArray()
                : [];
            var bound = tool.InputType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                .Concat(tool.HasSeparateId ? ["id"] : [])
                .Order().ToArray();

            Assert.True(bound.SequenceEqual(declared),
                $"{tool.Name}: schema has [{string.Join(", ", declared)}], the bound input has [{string.Join(", ", bound)}].");
            Assert.Equal(JsonValueKind.False, schema.GetProperty("additionalProperties").ValueKind);
            Assert.Equal("object", tool.OutputSchema.GetProperty("type").GetString());
        }
    }
}
