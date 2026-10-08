using System.Net;
using System.Net.Http.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

[Collection(XerpCollection.Name)]
public class ErrorModelTests(XerpFixture app)
{
    [Fact]
    public async Task AC70_Every_kind_of_error_is_a_problem_with_code_status_title_and_detail()
    {
        var tenant = await app.NewTenantAsync();
        await Uom.CreateAsync(tenant.Client, "kg", "Kilogram");

        using var validation = await Uom.PostAsync(tenant.Client, "a b", "Bad");
        using var unauthenticated = await app.Anonymous().GetAsync(Uom.Path);
        using var forbidden = await app.Admin().GetAsync(Uom.Path);
        using var notFound = await tenant.Client.GetAsync($"{Uom.Path}/{Guid.CreateVersion7()}");
        using var conflict = await Uom.PostAsync(tenant.Client, "KG", "Again");

        // HttpAssert.ProblemAsync checks content type, code, status, title and detail for each of them.
        var body = await HttpAssert.ValidationAsync(validation, "code");
        Assert.Equal("VALIDATION_FAILED", body.Str("title"));
        await HttpAssert.UnauthenticatedAsync(unauthenticated);
        await HttpAssert.ForbiddenAsync(forbidden);
        Assert.Equal("NOT_FOUND", (await HttpAssert.ProblemAsync(notFound, HttpStatusCode.NotFound, "NOT_FOUND")).Str("title"));
        Assert.Equal("CODE_TAKEN", (await HttpAssert.ProblemAsync(conflict, HttpStatusCode.Conflict, "CODE_TAKEN")).Str("title"));
    }

    [Theory]
    [InlineData("/api/v1/nope")]
    [InlineData("/api/v1/units-of-measure/by-code")]
    [InlineData("/api/v1/units-of-measure/by-code/kg/extra")]
    [InlineData("/api/v1/partners")] // was /api/v1/articles until spec 002 added that route
    [InlineData("/api/v1")]
    public async Task AC71_Unknown_path_under_api_v1_is_a_not_found_problem(string path)
    {
        var tenant = await app.NewTenantAsync();

        using var get = await tenant.Client.GetAsync(path);
        using var post = await tenant.Client.PostAsJsonAsync(path, new { });

        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(post);
    }

    [Fact]
    public async Task AC71_S1_Unknown_path_under_api_v1_still_requires_authentication()
    {
        using var response = await app.Anonymous().GetAsync("/api/v1/nope");

        await HttpAssert.UnauthenticatedAsync(response);
    }

    [Fact]
    public async Task AC71_Method_not_supported_on_a_known_path_is_a_problem_too()
    {
        var tenant = await app.NewTenantAsync();

        using var response = await tenant.Client.DeleteAsync(Uom.Path);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
