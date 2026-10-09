using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Partners;

namespace Xerp.Api.Endpoints;

public static class PartnerEndpoints
{
    public const string Route = "/partners";

    public static void MapPartnerEndpoints(this IEndpointRouteBuilder v1)
    {
        var partners = v1.MapGroup(Route);

        partners.MapGet("", async (HttpRequest request, PartnerOperations operations, CancellationToken ct) =>
        {
            var input = BindList(request.Query);
            if (!input.IsSuccess)
                return Problems.From(input.Error);
            var result = await operations.ListAsync(input.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "isCustomer", "isSupplier", "isActive", "limit", "offset");

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path.
        partners.MapGet("/{id:guid}", async (Guid id, PartnerOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        partners.MapGet("/by-code/{code}", async (string code, PartnerOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        partners.MapPost("", async (HttpRequest request, PartnerOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<PartnerInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        partners.MapPut("/{id:guid}", async (Guid id, HttpRequest request, PartnerOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<PartnerInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        partners.MapDelete("/{id:guid}", async (Guid id, PartnerOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }

    private static Result<ListPartnersInput> BindList(IQueryCollection query)
    {
        var binder = new QueryBinder(query);
        var input = new ListPartnersInput(binder.Text("search"), binder.Bool("isCustomer"), binder.Bool("isSupplier"), binder.Bool("isActive"),
            binder.Int("limit"), binder.Int("offset"));
        return binder.Error is { } error ? error : input;
    }
}
