using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002 as amended after review 001: control characters (R2, R3) and query strings (R17a).</summary>
[Collection(XerpCollection.Name)]
public class ArticleRequestInputTests(XerpFixture app)
{
    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\u0007b")]
    public async Task AC34_Description_with_a_forbidden_control_character_is_rejected(string description)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        var existing = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit.Id());

        using var created = await Art.PostAsync(tenant.Client, "A2", "Nut", unit.Id(), description: description);
        using var replaced = await Art.PutAsync(tenant.Client, existing.Id(), Art.ReplaceBody("A1", "Bolt", unit.Id(), description: description));

        await HttpAssert.ValidationAsync(created, "description");
        await HttpAssert.ValidationAsync(replaced, "description");
        using var list = await tenant.Client.GetAsync(Art.Path);
        Assert.Equal(["A1"], (await HttpAssert.JsonAsync(list, HttpStatusCode.OK)).Codes());
    }

    [Fact]
    public async Task AC34_Line_breaks_and_tabs_inside_a_description_are_kept()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");

        var article = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit.Id(), description: "a\r\n\tb");

        Assert.Equal("a\r\n\tb", article.Str("description"));
    }

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    public async Task AC34_Name_with_a_control_character_is_rejected_on_create_and_replace(string name)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        var existing = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit.Id());

        using var created = await Art.PostAsync(tenant.Client, "A2", name, unit.Id());
        using var replaced = await Art.PutAsync(tenant.Client, existing.Id(), Art.ReplaceBody("A1", name, unit.Id()));

        await HttpAssert.ValidationAsync(created, "name");
        await HttpAssert.ValidationAsync(replaced, "name");
        using var list = await tenant.Client.GetAsync(Art.Path);
        var items = await HttpAssert.JsonAsync(list, HttpStatusCode.OK);
        Assert.Equal(["A1"], items.Codes());
        Assert.Equal("Bolt", items.GetProperty("items")[0].Str("name"));
    }

    [Fact]
    public async Task AC73_Unknown_query_parameters_and_control_characters_in_search_are_rejected()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        var article = await Art.CreateAsync(tenant.Client, "A1", "Bolt", unit.Id());
        var id = article.Id();

        using var foo = await tenant.Client.GetAsync($"{Art.Path}?foo=1");
        using var wrongCase = await tenant.Client.GetAsync($"{Art.Path}?baseunitid={unit.Id()}");
        using var get = await tenant.Client.GetAsync($"{Art.Path}/{id}?x=1");
        using var byCode = await tenant.Client.GetAsync($"{Art.Path}/by-code/A1?x=1");
        using var post = await tenant.Client.PostAsync($"{Art.Path}?x=1", System.Net.Http.Json.JsonContent.Create(Art.Body("A2", "Nut", unit.Id())));
        using var put = await Art.PutAsync(tenant.Client, id, Art.ReplaceBody("A1", "Changed", unit.Id()));
        using var putWithQuery = await tenant.Client.PutAsync($"{Art.Path}/{id}?x=1", System.Net.Http.Json.JsonContent.Create(Art.ReplaceBody("A1", "Again", unit.Id())));
        using var delete = await tenant.Client.DeleteAsync($"{Art.Path}/{id}?x=1");
        using var search = await tenant.Client.GetAsync($"{Art.Path}?search=a%00b");

        await HttpAssert.ValidationAsync(foo, "foo");
        await HttpAssert.ValidationAsync(wrongCase, "baseunitid");
        foreach (var response in new[] { get, byCode, post, putWithQuery, delete })
            await HttpAssert.ValidationAsync(response, "x");
        await HttpAssert.ValidationAsync(search, "search");
        await HttpAssert.JsonAsync(put, HttpStatusCode.OK);
        using var list = await tenant.Client.GetAsync($"{Art.Path}?search=Changed&type=stock&baseUnitId={unit.Id()}&isActive=true&limit=5&offset=0");
        Assert.Equal(["A1"], (await HttpAssert.JsonAsync(list, HttpStatusCode.OK)).Codes());
    }
}
