using Xerp.Api.Http;
using Xerp.Application.Rules;

namespace Xerp.Api.Endpoints;

/// <summary>The configurable rules of the tenant (spec 012, section 4). Thin: bind, call, map - no rule is read here.</summary>
public static class RuleEndpoints
{
    public const string Route = "/rules";
    public const string ChangesRoute = "/rule-changes";

    public static void MapRuleEndpoints(this IEndpointRouteBuilder v1)
    {
        var rules = v1.MapGroup(Route);

        rules.MapGet("", async (HttpRequest request, RuleOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListRulesInput(binder.Text("search"), binder.Text("group"), binder.Text("source"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "group", "source", "limit", "offset");

        // The key addresses the rule: any text that is no rule of the registry is NOT_FOUND (R4).
        rules.MapGet("/{key}", async (string key, RuleOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(key, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        rules.MapPut("/{key}", async (string key, HttpRequest request, RuleOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetRuleInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.SetAsync(key, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        // No body: everything a reset needs is the rule itself.
        rules.MapPost("/{key}/reset", async (string key, RuleOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ResetAsync(key, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        v1.MapGet(ChangesRoute, async (HttpRequest request, RuleOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListRuleChangesInput(binder.Text("key"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListChangesAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("key", "limit", "offset");
    }
}
