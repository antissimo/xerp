using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 005, AC-70 to AC-73: an article or warehouse that a stock document uses — draft or posted — cannot be
/// deleted, and a used article's <c>type</c> and base unit are frozen.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockMasterEffectsTests(XerpFixture app)
{
    private static readonly MasterApi W = MasterApi.Warehouses;

    private static Task<HttpResponseMessage> DeleteArticleAsync(HttpClient client, Guid id) => client.DeleteAsync($"{Art.Path}/{id}");

    /// <summary>Makes article A and warehouse W1 used, by a draft or by a posted receipt; returns the document.</summary>
    private static async Task<JsonElement> UseAsync(StockSetup s, bool posted)
    {
        var draft = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.B, 1), (s.A, 1));
        return posted ? await Stock.PostDocumentAsync(s.Http, draft.Id()) : draft;
    }

    [Fact]
    public async Task AC70_An_article_on_a_draft_cannot_be_deleted_until_the_draft_is_deleted()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await UseAsync(s, posted: false);

        using var refused = await DeleteArticleAsync(s.Http, s.A);
        await Stock.ConflictAsync(refused, "IN_USE");
        McpAssert.JsonEqual(s.ArticleA, await Art.GetAsync(s.Http, s.A), "A refused delete changed the article");

        using var deletedDraft = await Stock.DeleteAsync(s.Http, draft.Id());
        using var deleted = await DeleteArticleAsync(s.Http, s.A);

        Assert.Equal(HttpStatusCode.NoContent, deletedDraft.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task AC70_R24_An_article_removed_from_every_draft_line_is_unused_again()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await UseAsync(s, posted: false);

        await Stock.ReplaceAsync(s.Http, draft.Id(), Stock.Replacement(s.W1, (s.B, 1)));
        using var deletedA = await DeleteArticleAsync(s.Http, s.A);
        using var refusedB = await DeleteArticleAsync(s.Http, s.B);

        Assert.Equal(HttpStatusCode.NoContent, deletedA.StatusCode);
        await Stock.ConflictAsync(refusedB, "IN_USE");
    }

    [Fact]
    public async Task AC70_R27_An_article_stays_used_while_another_draft_still_names_it()
    {
        var s = await Stock.SetupAsync(app);
        var first = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var second = await Stock.CreateAsync(s.Http, "issue", s.W2, (s.A, 1));

        using var deletedFirst = await Stock.DeleteAsync(s.Http, first.Id());
        using var refused = await DeleteArticleAsync(s.Http, s.A);
        using var deletedSecond = await Stock.DeleteAsync(s.Http, second.Id());
        using var deleted = await DeleteArticleAsync(s.Http, s.A);

        await Stock.ConflictAsync(refused, "IN_USE");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task AC71_An_article_on_a_posted_document_cannot_be_deleted_but_can_be_renamed_recoded_and_deactivated()
    {
        var s = await Stock.SetupAsync(app);
        await UseAsync(s, posted: true);

        using var refused = await DeleteArticleAsync(s.Http, s.A);
        await Stock.ConflictAsync(refused, "IN_USE");

        var body = Stock.ArticleBody(s.ArticleA);
        var renamed = await Stock.ReplaceArticleAsync(s.Http, s.A, body.With("name", "Renamed"));
        var recoded = await Stock.ReplaceArticleAsync(s.Http, s.A, body.With("code", "A-2"));
        var described = await Stock.ReplaceArticleAsync(s.Http, s.A, body.With("description", "Now described"));
        var retired = await Stock.ReplaceArticleAsync(s.Http, s.A, body.With("isActive", false));

        Assert.Equal("Renamed", renamed.Str("name"));
        Assert.Equal("A-2", recoded.Str("code"));
        Assert.Equal("Now described", described.Str("description"));
        Assert.False(retired.Bool("isActive"));
        Assert.Equal("stock", retired.Str("type"));
        Assert.Equal(s.UnitId, retired.GetProperty("baseUnit").Id());
        // Still used after all of that, and a retired article cannot be deleted either.
        using var stillRefused = await DeleteArticleAsync(s.Http, s.A);
        await Stock.ConflictAsync(stillRefused, "IN_USE");
        Assert.Equal(1m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC72_Type_and_base_unit_of_a_used_article_are_frozen(bool posted)
    {
        var s = await Stock.SetupAsync(app);
        var otherUnit = await Uom.CreateAsync(s.Http, "kg", "Kilogram");
        await UseAsync(s, posted);
        var body = () => Stock.ArticleBody(s.ArticleA);

        using var type = await Stock.PutArticleAsync(s.Http, s.A, body().With("type", "service"));
        using var unit = await Stock.PutArticleAsync(s.Http, s.A, body().With("baseUnitId", otherUnit.Id().ToString()));
        using var both = await Stock.PutArticleAsync(s.Http, s.A,
            body().With("type", "service").With("baseUnitId", otherUnit.Id().ToString()).With("name", "Also renamed"));

        var typeProblem = await Stock.ConflictAsync(type, "IN_USE", "type");
        Assert.DoesNotContain("baseUnitId", McpAssert.ErrorKeys(typeProblem));
        var unitProblem = await Stock.ConflictAsync(unit, "IN_USE", "baseUnitId");
        Assert.DoesNotContain("type", McpAssert.ErrorKeys(unitProblem));
        await Stock.ConflictAsync(both, "IN_USE", "type", "baseUnitId");
        McpAssert.JsonEqual(s.ArticleA, await Art.GetAsync(s.Http, s.A), "A refused replace changed the article");
    }

    [Fact]
    public async Task AC72_An_unused_article_can_still_change_type_and_base_unit()
    {
        var s = await Stock.SetupAsync(app);
        var otherUnit = await Uom.CreateAsync(s.Http, "kg", "Kilogram");
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 1); // A is used, B is not

        var changed = await Stock.ReplaceArticleAsync(s.Http, s.B,
            Stock.ArticleBody(s.ArticleB).With("type", "service").With("baseUnitId", otherUnit.Id().ToString()));

        Assert.Equal("service", changed.Str("type"));
        Assert.Equal(otherUnit.Id(), changed.GetProperty("baseUnit").Id());
    }

    [Fact]
    public async Task AC72_R26_The_freeze_is_checked_after_404_and_before_the_reference_checks()
    {
        var s = await Stock.SetupAsync(app);
        await UseAsync(s, posted: true);
        var body = () => Stock.ArticleBody(s.ArticleA);

        // A base unit that does not exist is still a change of the frozen field: IN_USE, not REFERENCE_NOT_FOUND.
        using var unknownUnit = await Stock.PutArticleAsync(s.Http, s.A, body().With("baseUnitId", Guid.NewGuid().ToString()));
        using var invalid = await Stock.PutArticleAsync(s.Http, s.A, body().With("type", "service").With("name", ""));
        using var notFound = await Stock.PutArticleAsync(s.Http, Guid.NewGuid(), body().With("type", "service"));

        await Stock.ConflictAsync(unknownUnit, "IN_USE", "baseUnitId");
        await HttpAssert.ValidationAsync(invalid, "name");
        await HttpAssert.NotFoundAsync(notFound);
    }

    [Fact]
    public async Task AC72_R27_Type_and_base_unit_can_change_again_once_the_last_draft_is_deleted()
    {
        var s = await Stock.SetupAsync(app);
        var draft = await UseAsync(s, posted: false);
        using (var frozen = await Stock.PutArticleAsync(s.Http, s.A, Stock.ArticleBody(s.ArticleA).With("type", "service")))
            await Stock.ConflictAsync(frozen, "IN_USE", "type");

        using var deleted = await Stock.DeleteAsync(s.Http, draft.Id());
        var changed = await Stock.ReplaceArticleAsync(s.Http, s.A, Stock.ArticleBody(s.ArticleA).With("type", "service"));

        Assert.Equal("service", changed.Str("type"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC73_A_used_warehouse_cannot_be_deleted_but_can_be_renamed_and_deactivated(bool posted)
    {
        var s = await Stock.SetupAsync(app);
        await UseAsync(s, posted);

        using var refused = await W.DeleteAsync(s.Http, s.W1);
        await Stock.ConflictAsync(refused, "IN_USE");
        await W.AssertUnchangedAsync(s.Http, s.Warehouse1);

        var changed = await W.ReplaceAsync(s.Http, s.W1,
            Stock.WarehouseBody(s.Warehouse1).With("name", "Renamed").With("code", "W1-B").With("isActive", false));
        using var stillRefused = await W.DeleteAsync(s.Http, s.W1);
        using var unused = await W.DeleteAsync(s.Http, s.W2);

        Assert.Equal("Renamed", changed.Str("name"));
        Assert.Equal("W1-B", changed.Str("code"));
        Assert.False(changed.Bool("isActive"));
        await Stock.ConflictAsync(stillRefused, "IN_USE");
        Assert.Equal(HttpStatusCode.NoContent, unused.StatusCode);
    }

    [Fact]
    public async Task AC73_R27_A_warehouse_is_unused_again_once_no_draft_names_it()
    {
        var s = await Stock.SetupAsync(app);
        var moved = await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1));
        var removed = await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 1));

        await Stock.ReplaceAsync(s.Http, moved.Id(), Stock.Replacement(s.W2, (s.A, 1)));
        using var refused = await W.DeleteAsync(s.Http, s.W1);
        using var deletedDraft = await Stock.DeleteAsync(s.Http, removed.Id());
        using var deleted = await W.DeleteAsync(s.Http, s.W1);
        using var w2Refused = await W.DeleteAsync(s.Http, s.W2);

        await Stock.ConflictAsync(refused, "IN_USE");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await Stock.ConflictAsync(w2Refused, "IN_USE");
    }

    [Fact]
    public async Task AC73_R28_The_base_unit_of_an_article_stays_in_use()
    {
        var s = await Stock.SetupAsync(app);
        await UseAsync(s, posted: true);

        using var refused = await s.Http.DeleteAsync($"{Uom.Path}/{s.UnitId}");

        await Stock.ConflictAsync(refused, "IN_USE");
    }
}
