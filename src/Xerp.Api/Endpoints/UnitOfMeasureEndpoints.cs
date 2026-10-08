using System.Globalization;
using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.UnitsOfMeasure;

namespace Xerp.Api.Endpoints;

public static class UnitOfMeasureEndpoints
{
    public const string Route = "/units-of-measure";

    public static void MapUnitOfMeasureEndpoints(this IEndpointRouteBuilder v1)
    {
        var units = v1.MapGroup(Route);

        units.MapGet("", async (HttpRequest request, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var input = BindList(request.Query);
            if (!input.IsSuccess)
                return Problems.From(input.Error);
            var result = await operations.ListAsync(input.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path (E6).
        units.MapGet("/{id:guid}", async (Guid id, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        units.MapGet("/by-code/{code}", async (string code, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        units.MapPost("", async (HttpRequest request, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateUnitOfMeasureInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        units.MapPut("/{id:guid}", async (Guid id, HttpRequest request, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplaceUnitOfMeasureInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        units.MapDelete("/{id:guid}", async (Guid id, UnitOfMeasureOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }

    /// <summary>
    /// Turns the query string into typed input. Only "is this an integer / a boolean" is decided here;
    /// ranges and lengths are Application rules. An empty value is the same as an absent one.
    /// </summary>
    private static Result<ListUnitsOfMeasureInput> BindList(IQueryCollection query)
    {
        var errors = new ValidationErrors();
        int? limit = null, offset = null;
        bool? isActive = null;

        if (Value(query, "limit") is { } limitText)
        {
            if (TryParseInt(limitText, out var parsed)) limit = parsed;
            else errors.Add("limit", "limit must be an integer.");
        }
        if (Value(query, "offset") is { } offsetText)
        {
            if (TryParseInt(offsetText, out var parsed)) offset = parsed;
            else errors.Add("offset", "offset must be an integer.");
        }
        if (Value(query, "isActive") is { } isActiveText)
        {
            if (isActiveText is "true") isActive = true;
            else if (isActiveText is "false") isActive = false;
            else errors.Add("isActive", "isActive must be true or false.");
        }
        if (query["search"].Count > 1)
            errors.Add("search", "search may be given only once.");

        if (errors.Any)
            return errors.ToError();
        return new ListUnitsOfMeasureInput(query["search"].ToString(), isActive, limit, offset);
    }

    private static string? Value(IQueryCollection query, string name)
    {
        var value = query[name].ToString(); // several values are joined with ',' and then fail to parse
        return value.Length == 0 ? null : value;
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
