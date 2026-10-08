using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 003, AC-40 to AC-45 (tool list and metadata) and AC-03 (MCP tools hold no data access).</summary>
[Collection(XerpCollection.Name)]
public class McpToolListTests(XerpFixture app)
{
    private sealed record Expected(string Name, string[] Properties, string[] Required, string Kind);

    // Spec 003, section 5.3. This is the complete list: a new tool must be added here by its spec.
    private static readonly Expected[] Tools =
    [
        new("whoami", [], [], "read"),
        new("uom_list", ["search", "isActive", "limit", "offset"], [], "read"),
        new("uom_get", ["id", "code"], [], "read"),
        new("uom_create", ["code", "name", "isActive"], ["code", "name"], "create"),
        new("uom_update", ["id", "code", "name", "isActive"], ["id", "code", "name", "isActive"], "update"),
        new("uom_delete", ["id"], ["id"], "delete"),
        new("article_list", ["search", "type", "baseUnitId", "isActive", "limit", "offset"], [], "read"),
        new("article_get", ["id", "code"], [], "read"),
        new("article_create", ["code", "name", "type", "baseUnitId", "description", "isActive"],
            ["code", "name", "type", "baseUnitId"], "create"),
        new("article_update", ["id", "code", "name", "description", "type", "baseUnitId", "isActive"],
            ["id", "code", "name", "description", "type", "baseUnitId", "isActive"], "update"),
        new("article_delete", ["id"], ["id"], "delete"),
        new("api_key_list", ["search", "actorType", "isActive", "limit", "offset"], [], "read"),
        new("api_key_get", ["id"], ["id"], "read"),
        new("api_key_revoke", ["id"], ["id"], "update"),
    ];

    private async Task<Dictionary<string, Tool>> ListToolsAsync(string? key = null)
    {
        key ??= (await app.NewTenantAsync()).Key;
        await using var mcp = await app.McpAsync(key);
        var tools = await mcp.Client.ListToolsAsync();
        return tools.ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool);
    }

    private static string[] Sorted(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task AC40_Tool_list_is_exactly_the_14_tools_of_the_spec()
    {
        var tools = await ListToolsAsync();

        Assert.Equal(14, Tools.Length);
        Assert.Equal(Sorted(Tools.Select(t => t.Name)), Sorted(tools.Keys));
        Assert.DoesNotContain("api_key_create", tools.Keys);
        Assert.DoesNotContain(tools.Keys, name => name.StartsWith("tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AC41_Every_tool_has_description_closed_input_schema_and_output_schema()
    {
        var tools = await ListToolsAsync();

        foreach (var expected in Tools)
        {
            Assert.True(tools.TryGetValue(expected.Name, out var tool), $"Tool '{expected.Name}' is not listed.");
            Assert.False(string.IsNullOrWhiteSpace(tool!.Description), $"{expected.Name}: description is empty.");

            var schema = tool.InputSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.TryGetProperty("additionalProperties", out var additional)
                && additional.ValueKind == JsonValueKind.False, $"{expected.Name}: inputSchema is not closed.");

            var properties = schema.TryGetProperty("properties", out var declared)
                ? declared.EnumerateObject().ToArray()
                : [];
            Assert.True(Sorted(expected.Properties).SequenceEqual(Sorted(properties.Select(p => p.Name))),
                $"{expected.Name}: properties are [{string.Join(", ", properties.Select(p => p.Name))}], expected [{string.Join(", ", expected.Properties)}].");
            foreach (var property in properties)
                Assert.True(property.Value.TryGetProperty("description", out var description)
                    && !string.IsNullOrWhiteSpace(description.GetString()),
                    $"{expected.Name}.{property.Name}: input property has no description.");

            var required = schema.TryGetProperty("required", out var requiredNames)
                ? requiredNames.EnumerateArray().Select(r => r.GetString()!).ToArray()
                : [];
            Assert.True(Sorted(expected.Required).SequenceEqual(Sorted(required)),
                $"{expected.Name}: required is [{string.Join(", ", required)}], expected [{string.Join(", ", expected.Required)}].");

            Assert.True(tool.OutputSchema is { ValueKind: JsonValueKind.Object }, $"{expected.Name}: no outputSchema.");
        }
    }

    [Fact]
    public async Task AC42_Every_tool_has_the_annotations_of_the_spec()
    {
        var tools = await ListToolsAsync();

        foreach (var expected in Tools)
        {
            Assert.True(tools.TryGetValue(expected.Name, out var tool), $"Tool '{expected.Name}' is not listed.");
            var a = tool!.Annotations;
            Assert.True(a is not null, $"{expected.Name}: no annotations.");
            var actual = (a!.ReadOnlyHint, a.DestructiveHint, a.IdempotentHint, a.OpenWorldHint);
            var ok = expected.Kind switch
            {
                // destructiveHint and idempotentHint are not specified for read-only tools.
                "read" => a.ReadOnlyHint == true && a.OpenWorldHint == false,
                "create" => actual == (false, false, false, false),
                "update" => actual == (false, true, true, false),
                "delete" => actual == (false, true, false, false),
                _ => false,
            };
            Assert.True(ok, $"{expected.Name} ({expected.Kind}): readOnly/destructive/idempotent/openWorld = {actual}.");
        }
    }

    [Theory]
    [InlineData("article_create", "type", "service,stock")]
    [InlineData("article_list", "type", "service,stock")]
    [InlineData("api_key_list", "actorType", "agent,human")]
    public async Task AC43_Enumerated_arguments_are_enums_in_the_input_schema(string toolName, string property, string values)
    {
        var tools = await ListToolsAsync();

        Assert.True(tools.TryGetValue(toolName, out var tool), $"Tool '{toolName}' is not listed.");
        var schema = tool!.InputSchema.GetProperty("properties").GetProperty(property);
        Assert.True(schema.TryGetProperty("enum", out var members), $"{toolName}.{property} has no enum: {schema}");
        Assert.Equal(values.Split(','), Sorted(members.EnumerateArray().Select(m => m.GetString()!)));
    }

    [Fact]
    public async Task AC44_Tool_list_and_instructions_are_the_same_for_every_tenant()
    {
        var a = await app.NewTenantAsync("Tenant Alpha");
        var b = await app.NewTenantAsync("Tenant Beta");
        await using var mcpA = await app.McpAsync(a.Key);
        await using var mcpB = await app.McpAsync(b.Key);

        var toolsA = JsonSerializer.SerializeToElement(
            (await mcpA.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToArray(), McpJsonUtilities.DefaultOptions);
        var toolsB = JsonSerializer.SerializeToElement(
            (await mcpB.Client.ListToolsAsync()).Select(t => t.ProtocolTool).ToArray(), McpJsonUtilities.DefaultOptions);

        Assert.NotEqual(0, toolsA.GetArrayLength());
        McpAssert.JsonEqual(toolsA, toolsB, "tools/list differs between tenants");
        Assert.Equal(mcpA.Client.ServerInstructions, mcpB.Client.ServerInstructions);
        // T6: static metadata contains no tenant data.
        foreach (var tenant in new[] { a, b })
            foreach (var secret in new[] { tenant.Id.ToString(), tenant.Code, tenant.Name, tenant.Key })
            {
                Assert.DoesNotContain(secret, toolsA.ToString());
                Assert.DoesNotContain(secret, mcpA.Client.ServerInstructions ?? "");
            }
    }

    [Theory]
    [InlineData("api_key_create")]
    [InlineData("tenant_create")]
    [InlineData("nope")]
    public async Task AC45_Unknown_tool_name_is_a_protocol_error_not_a_tool_result(string toolName)
    {
        var tenant = await app.NewTenantAsync();
        await using var mcp = await app.McpAsync(tenant.Key);

        CallToolResult? result = null;
        var failure = await Record.ExceptionAsync(async () =>
            result = await mcp.CallAsync(toolName, new { name = "bot", actorType = "agent" }));

        Assert.True(failure is not null, $"Expected a JSON-RPC error, got a tool result: {(result is null ? "" : McpAssert.Describe(result))}");
        Assert.IsAssignableFrom<McpException>(failure);
        // The connection is still usable and no key was created.
        await mcp.OkAsync("whoami");
        Assert.Equal(1, (await Keys.ListAsync(tenant.Client)).Total());
    }

    [Fact]
    public void AC03_Mcp_tool_types_hold_no_data_access()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = typeof(Program).Assembly.GetTypes()
            .Where(t => t.Namespace is "Xerp.Api.Mcp" || (t.Namespace?.StartsWith("Xerp.Api.Mcp.", StringComparison.Ordinal) ?? false))
            .ToList();

        Assert.True(types.Count > 0, "No types found in namespace Xerp.Api.Mcp: the MCP tools must live there (AC-03).");
        var violations = new List<string>();
        foreach (var type in types)
        {
            foreach (var constructor in type.GetConstructors(all))
                violations.AddRange(constructor.GetParameters().Where(p => IsDataAccess(p.ParameterType))
                    .Select(p => $"{type.FullName}..ctor({p.ParameterType.Name} {p.Name})"));
            foreach (var method in type.GetMethods(all))
                violations.AddRange(method.GetParameters().Where(p => IsDataAccess(p.ParameterType))
                    .Select(p => $"{type.FullName}.{method.Name}({p.ParameterType.Name} {p.Name})"));
            violations.AddRange(type.GetFields(all).Where(f => IsDataAccess(f.FieldType))
                .Select(f => $"{type.FullName}.{f.Name} : {f.FieldType.Name}"));
            violations.AddRange(type.GetProperties(all).Where(p => IsDataAccess(p.PropertyType))
                .Select(p => $"{type.FullName}.{p.Name} : {p.PropertyType.Name}"));
        }

        Assert.True(violations.Count == 0, "MCP types reference data access:\n" + string.Join("\n", violations));
    }

    private static bool IsDataAccess(Type type)
    {
        if (type.HasElementType)
            return IsDataAccess(type.GetElementType()!);
        if (type.IsGenericType && type.GetGenericArguments().Any(IsDataAccess))
            return true;
        if (type.Name == "IXerpDb" || type.GetInterfaces().Any(i => i.Name == "IXerpDb"))
            return true;
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false)
                return true;
        return type.GetInterfaces().Any(i => i.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false);
    }
}
