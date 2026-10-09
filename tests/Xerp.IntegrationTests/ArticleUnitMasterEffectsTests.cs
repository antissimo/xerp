using System.Net;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 007, AC-30 to AC-34: what conversions and line units freeze on the masters — the unit of measure
/// cannot be deleted, the article's base unit cannot change, a conversion used by a draft cannot be deleted —
/// and what they leave free.
/// </summary>
[Collection(XerpCollection.Name)]
public class ArticleUnitMasterEffectsTests(XerpFixture app)
{
    private static async Task AssertNoContentAsync(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 204, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    // ---- AC-30 ----

    [Fact]
    public async Task AC30_A_unit_with_a_conversion_cannot_be_deleted_until_the_conversion_is()
    {
        var s = await Units.SetupAsync(app);

        using var used = await Units.DeleteUnitAsync(s.Http, s.Box);
        await HttpAssert.InUseAsync(used);
        Assert.Equal("box", (await Uom.GetAsync(s.Http, s.Box)).Str("code"));
        Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());

        await Units.RemoveAsync(s.Http, s.A, s.Box);
        using var free = await Units.DeleteUnitAsync(s.Http, s.Box);

        await AssertNoContentAsync(free);
    }

    [Fact]
    public async Task AC30_R10_A_unit_stays_used_while_a_conversion_of_another_article_names_it()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.B, s.Box, 50);
        await Units.RemoveAsync(s.Http, s.A, s.Box);

        using var stillUsed = await Units.DeleteUnitAsync(s.Http, s.Box);
        await HttpAssert.InUseAsync(stillUsed);

        await Units.RemoveAsync(s.Http, s.B, s.Box);
        using var free = await Units.DeleteUnitAsync(s.Http, s.Box);
        await AssertNoContentAsync(free);
    }

    [Fact]
    public async Task AC30_R10_A_unit_with_a_conversion_can_still_be_renamed_recoded_and_deactivated()
    {
        var s = await Units.SetupAsync(app);

        using var replace = await Units.PutUnitAsync(s.Http, s.Box, "carton", "Carton", false);

        var unit = await HttpAssert.JsonAsync(replace, HttpStatusCode.OK);
        Assert.Equal(("carton", "Carton", false), (unit.Str("code"), unit.Str("name"), unit.Bool("isActive")));
        Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());
    }

    // ---- AC-31 ----

    [Fact]
    public async Task AC31_The_base_unit_of_an_article_with_conversions_is_frozen()
    {
        var s = await Units.SetupAsync(app);
        var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        await Units.SetAsync(s.Http, article.Id(), s.Box, 12);
        var before = await Art.GetAsync(s.Http, article.Id());

        using var change = await Stock.PutArticleAsync(s.Http, article.Id(), Stock.ArticleBody(before).With("baseUnitId", s.Pack.ToString()));

        await Stock.ConflictAsync(change, "IN_USE", "baseUnitId");
        McpAssert.JsonEqual(before, await Art.GetAsync(s.Http, article.Id()), "The article changed");

        // R9: type is not frozen by conversions; neither is anything else.
        var asService = await Stock.ReplaceArticleAsync(s.Http, article.Id(), Stock.ArticleBody(before).With("type", "service"));
        var renamed = await Stock.ReplaceArticleAsync(s.Http, article.Id(),
            Stock.ArticleBody(before).With("name", "Renamed").With("code", "C2").With("isActive", false));
        Assert.Equal("service", asService.Str("type"));
        Assert.Equal(("C2", "Renamed", "stock", false), (renamed.Str("code"), renamed.Str("name"), renamed.Str("type"), renamed.Bool("isActive")));
        Assert.Equal(s.Pcs, renamed.GetProperty("baseUnit").Id());

        await Units.RemoveAsync(s.Http, article.Id(), s.Box);
        var changed = await Stock.ReplaceArticleAsync(s.Http, article.Id(), Stock.ArticleBody(before).With("baseUnitId", s.Pack.ToString()));
        Assert.Equal(s.Pack, changed.GetProperty("baseUnit").Id());
    }

    [Fact]
    public async Task AC31_R9_The_freeze_is_checked_after_validation_and_404_and_before_the_reference_checks()
    {
        // "As 005/R26, same place in the order of checks" (005-q T-Q3): validation -> 404 -> frozen -> references.
        var s = await Units.SetupAsync(app);
        var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        await Units.SetAsync(s.Http, article.Id(), s.Box, 12);
        var before = await Art.GetAsync(s.Http, article.Id());
        var body = Stock.ArticleBody(before);

        using var invalid = await Stock.PutArticleAsync(s.Http, article.Id(), Stock.ArticleBody(before).With("baseUnitId", s.Pack.ToString()).With("name", ""));
        using var unknownArticle = await Stock.PutArticleAsync(s.Http, Guid.NewGuid(), Stock.ArticleBody(before).With("baseUnitId", s.Pack.ToString()));
        using var unknownUnit = await Stock.PutArticleAsync(s.Http, article.Id(), Stock.ArticleBody(before).With("baseUnitId", Guid.NewGuid().ToString()));
        // The unit that is the article's own alternative unit is no exception.
        using var ownAlternative = await Stock.PutArticleAsync(s.Http, article.Id(), body.With("baseUnitId", s.Box.ToString()));

        await HttpAssert.ValidationAsync(invalid, "name");
        await HttpAssert.NotFoundAsync(unknownArticle);
        await Stock.ConflictAsync(unknownUnit, "IN_USE", "baseUnitId");
        await Stock.ConflictAsync(ownAlternative, "IN_USE", "baseUnitId");
        McpAssert.JsonEqual(before, await Art.GetAsync(s.Http, article.Id()), "The article changed");
    }

    [Fact]
    public async Task AC31_E10_Changing_the_base_unit_delete_the_conversions_change_set_them_again()
    {
        var s = await Units.SetupAsync(app);
        var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        await Units.SetAsync(s.Http, article.Id(), s.Box, 12);
        await Units.SetAsync(s.Http, article.Id(), s.Pack, 6);

        await Units.RemoveAsync(s.Http, article.Id(), s.Box);
        using (var oneLeft = await Stock.PutArticleAsync(s.Http, article.Id(), Stock.ArticleBody(article).With("baseUnitId", s.Box.ToString())))
            await Stock.ConflictAsync(oneLeft, "IN_USE", "baseUnitId");
        await Units.RemoveAsync(s.Http, article.Id(), s.Pack);
        await Stock.ReplaceArticleAsync(s.Http, article.Id(), Stock.ArticleBody(article).With("baseUnitId", s.Box.ToString()));

        // The old base unit is now an ordinary alternative unit; the new base unit cannot be one.
        using var oldBase = await Units.PutAsync(s.Http, article.Id(), s.Pcs, 0.083333m);
        using var newBase = await Units.PutAsync(s.Http, article.Id(), s.Box, 1);
        var conversion = await HttpAssert.JsonAsync(oldBase, HttpStatusCode.Created);
        Assert.Equal(("pcs", "box", 0.083333m),
            (conversion.GetProperty("unit").Str("code"), conversion.GetProperty("baseUnit").Str("code"), conversion.Factor()));
        await Stock.ConflictAsync(newBase, "UNIT_IS_BASE_UNIT", "unitId");
    }

    // ---- AC-32 ----

    [Fact]
    public async Task AC32_An_article_is_deleted_together_with_its_conversions()
    {
        var s = await Units.SetupAsync(app);
        var crate = await Uom.CreateAsync(s.Http, "crate", "Crate");
        var bag = await Uom.CreateAsync(s.Http, "bag", "Bag");
        var article = await Art.CreateAsync(s.Http, "C", "Article C", s.Pcs);
        await Units.SetAsync(s.Http, article.Id(), crate.Id(), 100);
        await Units.SetAsync(s.Http, article.Id(), bag.Id(), 5);

        using var delete = await s.Http.DeleteAsync($"{Art.Path}/{article.Id()}");
        await AssertNoContentAsync(delete);

        using var list = await s.Http.GetAsync(Units.Path(article.Id()));
        using var get = await Units.SendGetAsync(s.Http, article.Id(), crate.Id());
        await HttpAssert.NotFoundAsync(list);
        await HttpAssert.NotFoundAsync(get);
        Assert.Equal(0, (await Art.ListAsync(s.Http, $"?alternativeUnitId={crate.Id()}")).Total());
        // R11: the units are unused by the deleted conversions.
        using var deleteCrate = await Units.DeleteUnitAsync(s.Http, crate.Id());
        using var deleteBag = await Units.DeleteUnitAsync(s.Http, bag.Id());
        await AssertNoContentAsync(deleteCrate);
        await AssertNoContentAsync(deleteBag);
        // Another article's conversion is untouched.
        Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());
    }

    [Fact]
    public async Task AC32_An_article_used_by_a_stock_document_keeps_its_conversions_when_its_delete_is_refused()
    {
        var s = await Units.SetupAsync(app);
        await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 1, null));

        using var delete = await s.Http.DeleteAsync($"{Art.Path}/{s.A}");

        await HttpAssert.InUseAsync(delete);
        Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());
    }

    // ---- AC-33 ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AC33_A_conversion_used_by_a_draft_line_cannot_be_deleted(bool replace)
    {
        var s = await Units.SetupAsync(app);
        var draft = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.B, 1, null), (s.A, 5, s.Box));

        using var used = await Units.DeleteAsync(s.Http, s.A, s.Box);

        await HttpAssert.InUseAsync(used);
        Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());
        await Stock.AssertUnchangedAsync(s.Http, draft);

        if (replace)
            await Stock.ReplaceAsync(s.Http, draft.Id(), Units.Replacement(s.W1, (s.B, 1, null), (s.A, 60, null)));
        else
        {
            using var deleteDraft = await Stock.DeleteAsync(s.Http, draft.Id());
            await AssertNoContentAsync(deleteDraft);
        }
        using var free = await Units.DeleteAsync(s.Http, s.A, s.Box);
        await AssertNoContentAsync(free);
    }

    [Fact]
    public async Task AC33_R8_A_conversion_stays_used_while_another_draft_still_has_a_line_in_that_unit()
    {
        var s = await Units.SetupAsync(app);
        var first = await Units.CreateAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));
        var second = await Units.CreateAsync(s.Http, "issue", s.W2, (s.A, 1, s.Box));
        // A draft in boxes of another article does not use A's conversion.
        await Units.SetAsync(s.Http, s.B, s.Box, 50);
        await Units.CreateAsync(s.Http, "receipt", s.W1, (s.B, 1, s.Box));

        using (var deleteFirst = await Stock.DeleteAsync(s.Http, first.Id()))
            await AssertNoContentAsync(deleteFirst);
        using var stillUsed = await Units.DeleteAsync(s.Http, s.A, s.Box);
        await HttpAssert.InUseAsync(stillUsed);

        using (var deleteSecond = await Stock.DeleteAsync(s.Http, second.Id()))
            await AssertNoContentAsync(deleteSecond);
        using var free = await Units.DeleteAsync(s.Http, s.A, s.Box);
        await AssertNoContentAsync(free);
        using var ofB = await Units.DeleteAsync(s.Http, s.B, s.Box);
        await HttpAssert.InUseAsync(ofB);
    }

    [Fact]
    public async Task AC33_R8_Saving_a_draft_and_deleting_its_conversion_in_parallel_never_leaves_a_line_without_its_unit()
    {
        // Section 11: either the draft is saved and the delete gets IN_USE, or the delete succeeds and the save
        // gets UNIT_NOT_ON_ARTICLE.
        for (var round = 0; round < 4; round++)
        {
            var s = await Units.SetupAsync(app);
            var save = Task.Run(() => Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 5, s.Box))));
            var delete = Task.Run(() => Units.DeleteAsync(s.Http, s.A, s.Box));
            using var saved = await save;
            using var deleted = await delete;

            if (saved.StatusCode == HttpStatusCode.Created)
            {
                var draft = await HttpAssert.JsonAsync(saved, HttpStatusCode.Created);
                await HttpAssert.InUseAsync(deleted);
                Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());
                Units.AssertLine((await Stock.GetAsync(s.Http, draft.Id())).DocumentLines()[0], "box", 5m, 12m, 60m);
            }
            else
            {
                await Stock.ConflictAsync(saved, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
                await AssertNoContentAsync(deleted);
                Assert.Equal(0, (await Stock.DocumentsAsync(s.Http)).Total());
                Assert.Null(await Units.FactorOrNullAsync(s.Http, s.A, s.Box));
            }
        }
    }

    // ---- AC-34 ----

    [Fact]
    public async Task AC34_A_posted_line_does_not_block_deleting_the_conversion_but_keeps_the_unit_used()
    {
        var s = await Units.SetupAsync(app);
        var posted = await Units.PostedAsync(s.Http, "receipt", s.W1, (s.A, 5, s.Box));
        var entries = await Stock.EntriesAsync(s.Http, posted.Id());

        using var deleteConversion = await Units.DeleteAsync(s.Http, s.A, s.Box);

        await AssertNoContentAsync(deleteConversion);
        var after = await Stock.GetAsync(s.Http, posted.Id());
        Units.AssertLine(after.DocumentLines()[0], "box", 5m, 12m, 60m);
        McpAssert.JsonEqual(posted, after, "The posted document changed");
        McpAssert.JsonEqual(entries[0], (await Stock.EntriesAsync(s.Http, posted.Id()))[0], "The ledger entry changed");
        Assert.Equal(60m, await Stock.QuantityAsync(s.Http, s.A, s.W1));
        // R10: the line still names the unit of measure.
        using var deleteUnit = await Units.DeleteUnitAsync(s.Http, s.Box);
        await HttpAssert.InUseAsync(deleteUnit);
        // A new line can no longer be entered in boxes.
        using var newLine = await Stock.PostAsync(s.Http, Units.Draft("receipt", s.W1, (s.A, 1, s.Box)));
        await Stock.ConflictAsync(newLine, "UNIT_NOT_ON_ARTICLE", "lines[0].unitId");
    }
}
