using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Xerp.Api.Mcp;

/// <summary>
/// JSON Schemas of tools. Output schemas are generated from the DTO the operation returns, so they cannot
/// drift from the HTTP representation; input schemas are closed and carry hand-written descriptions
/// (a unit test compares their property names with the bound input type).
/// </summary>
public static class ToolSchemas
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly JsonSchemaExporterOptions Exporter = new() { TreatNullObliviousAsNonNullable = true };

    public static JsonObject Text(string description, params string[] oneOf)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description };
        if (oneOf.Length > 0)
            schema["enum"] = new JsonArray(oneOf.Select(v => (JsonNode)v).ToArray());
        return schema;
    }

    public static JsonObject NullableText(string description) =>
        new() { ["type"] = new JsonArray("string", "null"), ["description"] = description };

    public static JsonObject Uuid(string description) =>
        new() { ["type"] = "string", ["format"] = "uuid", ["description"] = description };

    public static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject Integer(string description, int minimum, int? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum };
        if (maximum is { } max)
            schema["maximum"] = max;
        return schema;
    }

    /// <summary>A JSON number; a quoted number is a wrong type.</summary>
    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    /// <summary>An array of closed objects with exactly the listed properties, all required.</summary>
    public static JsonObject ArrayOf(string description, int minItems, int maxItems, params (string Name, JsonObject Schema)[] properties)
    {
        var declared = new JsonObject();
        foreach (var (name, property) in properties)
            declared[name] = property.DeepClone();
        return new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["minItems"] = minItems,
            ["maxItems"] = maxItems,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = declared,
                ["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Name).ToArray()),
                ["additionalProperties"] = false,
            },
        };
    }

    /// <summary>A closed object schema: no argument other than the listed ones is accepted.</summary>
    public static JsonElement Input(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var schema = new JsonObject { ["type"] = "object" };
        var declared = new JsonObject();
        foreach (var (name, property) in properties)
            declared[name] = property.DeepClone(); // property definitions are shared between tools
        schema["properties"] = declared;
        schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        schema["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(schema);
    }

    public static JsonElement Output(Type type) =>
        JsonSerializer.SerializeToElement(Web.GetJsonSchemaAsNode(type, Exporter));
}
