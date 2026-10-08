using System.Net;
using System.Net.Http.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>Spec 002: a unit of measure referenced by an article cannot be deleted (AC-80 … AC-85).</summary>
[Collection(XerpCollection.Name)]
public class UnitOfMeasureInUseTests(XerpFixture app)
{
    [Theory]
    [InlineData(true)]  // AC-80
    [InlineData(false)] // AC-81: an inactive referrer counts too
    public async Task AC80_AC81_Referenced_unit_cannot_be_deleted(bool articleIsActive)
    {
        var tenant = await app.NewTenantAsync();
        var unit = await Uom.CreateAsync(tenant.Client, "pcs", "Piece");
        await ArticleApi.CreateAsync(tenant.Client, "A1", "Bolt", unit.Id(), isActive: articleIsActive);

        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit.Id()}");

        await HttpAssert.InUseAsync(delete);
        Assert.Equal(unit.ToString(), (await Uom.GetAsync(tenant.Client, unit.Id())).ToString());
    }

    [Fact]
    public async Task AC82_Unit_can_be_deleted_once_its_only_article_is_deleted()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await ArticleApi.UnitAsync(tenant.Client);
        var article = await ArticleApi.CreateAsync(tenant.Client, "A1", "Bolt", unit);
        using (var blocked = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit}"))
            await HttpAssert.InUseAsync(blocked);

        using (var deleteArticle = await tenant.Client.DeleteAsync($"{ArticleApi.Path}/{article.Id()}"))
            Assert.Equal(HttpStatusCode.NoContent, deleteArticle.StatusCode);
        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var get = await tenant.Client.GetAsync($"{Uom.Path}/{unit}");
        await HttpAssert.NotFoundAsync(get);
    }

    [Fact]
    public async Task AC82_Unit_can_be_deleted_once_its_only_article_moves_to_another_unit()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await ArticleApi.UnitAsync(tenant.Client, "pcs", "Piece");
        var other = await ArticleApi.UnitAsync(tenant.Client, "kg", "Kilogram");
        var article = await ArticleApi.CreateAsync(tenant.Client, "A1", "Bolt", unit);

        using (var move = await ArticleApi.PutAsync(tenant.Client, article.Id(), ArticleApi.ReplaceBody("A1", "Bolt", other)))
            Assert.Equal(HttpStatusCode.OK, move.StatusCode);
        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{unit}");
        using var deleteOther = await tenant.Client.DeleteAsync($"{Uom.Path}/{other}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await HttpAssert.InUseAsync(deleteOther);
    }

    [Fact]
    public async Task AC83_Unreferenced_unit_is_deleted_as_before()
    {
        var tenant = await app.NewTenantAsync();
        var used = await ArticleApi.UnitAsync(tenant.Client, "pcs", "Piece");
        var unused = await ArticleApi.UnitAsync(tenant.Client, "kg", "Kilogram");
        await ArticleApi.CreateAsync(tenant.Client, "A1", "Bolt", used);

        using var delete = await tenant.Client.DeleteAsync($"{Uom.Path}/{unused}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await delete.Content.ReadAsByteArrayAsync());
        Assert.Equal(["pcs"], (await Uom.ListAsync(tenant.Client)).Codes());
    }

    [Fact]
    public async Task AC84_Referenced_unit_can_be_deactivated_and_renamed()
    {
        var tenant = await app.NewTenantAsync();
        var unit = await ArticleApi.UnitAsync(tenant.Client, "pcs", "Piece");
        var article = await ArticleApi.CreateAsync(tenant.Client, "A1", "Bolt", unit);

        using var response = await tenant.Client.PutAsJsonAsync($"{Uom.Path}/{unit}", new { code = "kom", name = "Komad", isActive = false });

        var updated = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
        Assert.Equal("kom", updated.Str("code"));
        Assert.Equal("Komad", updated.Str("name"));
        Assert.False(updated.GetProperty("isActive").GetBoolean());
        var after = await ArticleApi.GetAsync(tenant.Client, article.Id());
        Assert.Equal(unit, after.BaseUnitId());
        Assert.Equal("kom", after.GetProperty("baseUnit").Str("code"));
    }

    [Fact]
    public async Task AC85_Create_article_racing_with_delete_of_its_unit_never_leaves_a_dangling_reference()
    {
        var tenant = await app.NewTenantAsync();
        var created = 0;

        for (var round = 0; round < 20; round++)
        {
            var unit = await ArticleApi.UnitAsync(tenant.Client, $"u{round}", $"Unit {round}");

            // Alternate which request is sent first so that both orders occur.
            Task<HttpResponseMessage> postTask, deleteTask;
            if (round % 2 == 0)
            {
                postTask = ArticleApi.PostAsync(tenant.Client, $"A{round}", "Racer", unit);
                deleteTask = tenant.Client.DeleteAsync($"{Uom.Path}/{unit}");
            }
            else
            {
                deleteTask = tenant.Client.DeleteAsync($"{Uom.Path}/{unit}");
                postTask = ArticleApi.PostAsync(tenant.Client, $"A{round}", "Racer", unit);
            }
            using var post = await postTask;
            using var delete = await deleteTask;

            using var getUnit = await tenant.Client.GetAsync($"{Uom.Path}/{unit}");
            if (post.StatusCode == HttpStatusCode.Created)
            {
                created++;
                await HttpAssert.InUseAsync(delete);
                Assert.Equal(HttpStatusCode.OK, getUnit.StatusCode);
            }
            else
            {
                await HttpAssert.ReferenceNotFoundAsync(post, "baseUnitId");
                Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
                await HttpAssert.NotFoundAsync(getUnit);
            }
        }

        // Every article that exists points at a unit that exists.
        var articles = await ArticleApi.ListAsync(tenant.Client, "?limit=500");
        Assert.Equal(created, articles.Total());
        var units = (await Uom.ListAsync(tenant.Client, "?limit=500")).GetProperty("items").EnumerateArray().Select(u => u.Id()).ToHashSet();
        Assert.Equal(created, units.Count);
        Assert.All(articles.GetProperty("items").EnumerateArray(), a => Assert.Contains(a.BaseUnitId(), units));
    }
}
