using Xerp.Api.Http;
using Xerp.Application.Identity;
using Xerp.Application.Tenants;

namespace Xerp.Api.Endpoints;

public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapPost("/admin/tenants", async (HttpRequest request, TenantProvisioning tenants, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateTenantInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await tenants.CreateAsync(body.Value, ct);
            return result.IsSuccess ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created) : Problems.From(result.Error);
        });

        v1.MapGet("/whoami", async (WhoAmIOperation whoAmI, CancellationToken ct) =>
        {
            var result = await whoAmI.ExecuteAsync(ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }
}
