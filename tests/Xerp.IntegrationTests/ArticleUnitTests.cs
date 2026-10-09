using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 007, AC-10 and AC-20 to AC-29: article units (conversions) — set, get, list, delete, the factor rules,
/// references and the article filter <c>alternativeUnitId</c>.
/// </summary>
[Collection(XerpCollection.Name)]
public class ArticleUnitTests(XerpFixture app)
{
    private static async Task AssertConversionsAsync(UnitSetup s, Guid article, params (string Unit, decimal Factor)[] expected)
    {
        var list = await Units.ListAsync(s.Http, article);
        Assert.Equal(expected.Length, list.Total());
        Assert.Equal(expected, list.Items().Select(i => (i.GetProperty("unit").Str("code"), i.Factor())).ToArray());
    }

    // ---- AC-10 ----

    [Fact]
    public async Task AC10_Article_units_need_a_tenant_key()
    {
        var s = await Units.SetupAsync(app);
        using var anonymous = app.Anonymous();
        using var admin = app.Admin();

        using var anonymousList = await anonymous.GetAsync(Units.Path(s.A));
        using var anonymousSet = await Units.PutAsync(anonymous, s.A, s.Box, 5);
        using var adminList = await admin.GetAsync(Units.Path(s.A));
        using var adminSet = await Units.PutAsync(admin, s.A, s.Box, 5);

        await HttpAssert.UnauthenticatedAsync(anonymousList);
        await HttpAssert.UnauthenticatedAsync(anonymousSet);
        await HttpAssert.ForbiddenAsync(adminList);
        await HttpAssert.ForbiddenAsync(adminSet);
        await AssertConversionsAsync(s, s.A, ("box", 12m));
    }

    [Fact]
    public async Task AC10_Unknown_query_parameter_and_unknown_body_property_are_rejected()
    {
        var s = await Units.SetupAsync(app);

        using var query = await s.Http.GetAsync(Units.Path(s.A) + "?x=1");
        using var isActive = await Units.PutAsync(s.Http, s.A, s.Box, Units.Body(12).With("isActive", true));
        // E1: the unit is given by the path only.
        using var unitId = await Units.PutAsync(s.Http, s.A, s.Pack, Units.Body(6).With("unitId", s.Pack.ToString()));

        await HttpAssert.ValidationAsync(query, "x");
        await HttpAssert.ValidationAsync(isActive);
        await HttpAssert.ValidationAsync(unitId);
        await AssertConversionsAsync(s, s.A, ("box", 12m));
    }

    // ---- AC-20, AC-21 ----

    [Fact]
    public async Task AC20_Set_creates_a_conversion_with_summaries_and_get_returns_the_same()
    {
        var s = await Units.SetupAsync(app);

        using var response = await Units.PutAsync(s.Http, s.A, s.Pack, 6);

        var created = await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal(s.A, created.GetProperty("article").Id());
        Assert.Equal(("A", "Article A"), (created.GetProperty("article").Str("code"), created.GetProperty("article").Str("name")));
        Assert.Equal(s.Pack, created.GetProperty("unit").Id());
        Assert.Equal(("pack", "Pack"), (created.GetProperty("unit").Str("code"), created.GetProperty("unit").Str("name")));
        Assert.Equal(6m, created.Factor());
        Assert.Equal(s.Pcs, created.GetProperty("baseUnit").Id());
        Assert.Equal("pcs", created.GetProperty("baseUnit").Str("code"));
        Assert.Equal(s.Tenant.ApiKeyId, created.GetProperty("createdBy").GetGuid());
        Assert.Equal(s.Tenant.ApiKeyId, created.GetProperty("updatedBy").GetGuid());
        Assert.Equal(JsonValueKind.String, created.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, created.GetProperty("updatedAt").ValueKind);
        Assert.False(created.TryGetProperty("tenantId", out _), "The representation must not contain tenantId.");
        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/v1/articles/{s.A}/units/{s.Pack}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        McpAssert.JsonEqual(created, await Units.GetAsync(s.Http, s.A, s.Pack), "GET differs from the created conversion");
    }

    [Fact]
    public async Task AC21_Set_again_replaces_the_factor_and_is_attributed_to_the_acting_key()
    {
        var s = await Units.SetupAsync(app);
        var k2 = await Keys.CreateAsync(app, s.Http, "second", "human");
        using (var first = await Units.PutAsync(s.Http, s.A, s.Pack, 6))
            await HttpAssert.JsonAsync(first, HttpStatusCode.Created);
        var created = await Units.GetAsync(s.Http, s.A, s.Pack);

        using var replace = await Units.PutAsync(k2.Client, s.A, s.Pack, 8);
        var replaced = await HttpAssert.JsonAsync(replace, HttpStatusCode.OK);
        using var repeat = await Units.PutAsync(k2.Client, s.A, s.Pack, 8);
        var repeated = await HttpAssert.JsonAsync(repeat, HttpStatusCode.OK);

        Assert.Equal(8m, replaced.Factor());
        Assert.Equal(k2.Id, replaced.GetProperty("updatedBy").GetGuid());
        Assert.Equal(created.Str("createdAt"), replaced.Str("createdAt"));
        Assert.Equal(s.Tenant.ApiKeyId, replaced.GetProperty("createdBy").GetGuid());
        Assert.Equal(8m, repeated.Factor());
        Assert.Equal(created.Str("createdAt"), repeated.Str("createdAt"));
        Assert.Equal(s.Tenant.ApiKeyId, repeated.GetProperty("createdBy").GetGuid());
        // R3: still one conversion per (article, unit).
        await AssertConversionsAsync(s, s.A, ("box", 12m), ("pack", 8m));
    }

    // ---- AC-22 ----

    [Fact]
    public async Task AC22_List_is_ordered_by_unit_code_and_has_no_base_unit()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 6);

        var ofA = await Units.ListAsync(s.Http, s.A);
        var ofB = await Units.ListAsync(s.Http, s.B);
        using var unknown = await s.Http.GetAsync(Units.Path(Guid.NewGuid()));
        using var malformed = await s.Http.GetAsync($"{Art.Path}/abc/units");

        Assert.Equal(2, ofA.Total());
        Assert.Equal(new[] { "box", "pack" }, ofA.UnitCodes());
        Assert.DoesNotContain("pcs", ofA.UnitCodes());
        foreach (var item in ofA.Items())
            McpAssert.JsonEqual(await Units.GetAsync(s.Http, s.A, item.GetProperty("unit").Id()), item, "A list item differs from GET");
        Assert.Equal(0, ofB.Total());
        Assert.Empty(ofB.Items());
        await HttpAssert.NotFoundAsync(unknown);
        await HttpAssert.NotFoundAsync(malformed);
    }

    [Fact]
    public async Task AC22_List_orders_case_insensitively_and_pages()
    {
        // Section 4.1: "ordered by unit code (case-insensitive)".
        var s = await Units.SetupAsync(app);
        foreach (var (code, factor) in new[] { ("Crate", 100m), ("bag", 3m), ("ZZ", 7m) })
            await Units.SetAsync(s.Http, s.A, (await Uom.CreateAsync(s.Http, code, code)).Id(), factor);

        var all = await Units.ListAsync(s.Http, s.A);
        var page = await Units.ListAsync(s.Http, s.A, "?limit=2&offset=1");

        Assert.Equal(new[] { "bag", "box", "Crate", "ZZ" }, all.UnitCodes());
        Assert.Equal(4, page.Total());
        Assert.Equal(new[] { "box", "Crate" }, page.UnitCodes());
    }

    // ---- AC-23 ----

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "factor": null }""")]
    [InlineData("""{ "factor": 0 }""")]
    [InlineData("""{ "factor": -1 }""")]
    [InlineData("""{ "factor": "12" }""")]
    [InlineData("""{ "factor": 1.0000001 }""")]
    [InlineData("""{ "factor": 1000000 }""")]
    public async Task AC23_Invalid_factor_is_rejected_with_its_key_and_changes_nothing(string json)
    {
        var s = await Units.SetupAsync(app);

        using var create = await s.Http.PutAsync(Units.Path(s.A, s.Pack), HttpAssert.Raw(json));
        using var replace = await s.Http.PutAsync(Units.Path(s.A, s.Box), HttpAssert.Raw(json));

        await HttpAssert.ValidationAsync(create, "factor");
        await HttpAssert.ValidationAsync(replace, "factor");
        await AssertConversionsAsync(s, s.A, ("box", 12m));
    }

    [Theory]
    [InlineData("0.000001")]
    [InlineData("999999.999999")]
    [InlineData("0.5")]
    public async Task AC23_The_smallest_and_the_largest_factor_are_accepted_and_kept_exactly(string factor)
    {
        var s = await Units.SetupAsync(app);
        var value = decimal.Parse(factor, System.Globalization.CultureInfo.InvariantCulture);

        using var response = await s.Http.PutAsync(Units.Path(s.A, s.Pack), HttpAssert.Raw($$"""{ "factor": {{factor}} }"""));

        Assert.Equal(value, (await HttpAssert.JsonAsync(response, HttpStatusCode.Created)).Factor());
        Assert.Equal(value, (await Units.GetAsync(s.Http, s.A, s.Pack)).Factor());
    }

    // ---- AC-24, AC-25 ----

    [Fact]
    public async Task AC24_References_on_set()
    {
        var s = await Units.SetupAsync(app);
        var inactive = await Uom.CreateAsync(s.Http, "bag", "Bag", isActive: false);

        using var randomUnit = await Units.PutAsync(s.Http, s.A, Guid.NewGuid(), 6);
        using var inactiveUnit = await Units.PutAsync(s.Http, s.A, inactive.Id(), 6);
        using var baseUnit = await Units.PutAsync(s.Http, s.A, s.Pcs, 6);
        using var randomArticle = await Units.PutAsync(s.Http, Guid.NewGuid(), s.Pack, 6);
        using var malformedUnit = await s.Http.PutAsync($"{Units.Path(s.A)}/abc", HttpAssert.Raw("""{ "factor": 6 }"""));
        using var malformedArticle = await s.Http.PutAsync($"{Art.Path}/abc/units/{s.Pack}", HttpAssert.Raw("""{ "factor": 6 }"""));

        await HttpAssert.ReferenceNotFoundAsync(randomUnit, "unitId");
        await HttpAssert.ReferenceInactiveAsync(inactiveUnit, "unitId");
        await Stock.ConflictAsync(baseUnit, "UNIT_IS_BASE_UNIT", "unitId");
        await HttpAssert.NotFoundAsync(randomArticle);
        await HttpAssert.NotFoundAsync(malformedUnit);
        await HttpAssert.NotFoundAsync(malformedArticle);
        await AssertConversionsAsync(s, s.A, ("box", 12m));
    }

    [Fact]
    public async Task AC24_R6_Order_of_checks_on_set()
    {
        var s = await Units.SetupAsync(app);

        // Validation before the article's existence.
        using var invalidOnUnknownArticle = await Units.PutAsync(s.Http, Guid.NewGuid(), s.Pack, 0);
        // The article's existence before the unit's (E2).
        using var bothUnknown = await Units.PutAsync(s.Http, Guid.NewGuid(), Guid.NewGuid(), 6);
        // Validation before the unit's existence and before the base unit check.
        using var invalidOnUnknownUnit = await Units.PutAsync(s.Http, s.A, Guid.NewGuid(), 0);
        using var invalidOnBaseUnit = await Units.PutAsync(s.Http, s.A, s.Pcs, -1);
        // "Not the base unit" before "active": the base unit, deactivated, is still the base unit.
        await Units.SetUnitActiveAsync(s.Http, s.S.Unit, false);
        using var inactiveBaseUnit = await Units.PutAsync(s.Http, s.A, s.Pcs, 6);

        await HttpAssert.ValidationAsync(invalidOnUnknownArticle, "factor");
        await HttpAssert.NotFoundAsync(bothUnknown);
        await HttpAssert.ValidationAsync(invalidOnUnknownUnit, "factor");
        await HttpAssert.ValidationAsync(invalidOnBaseUnit, "factor");
        await Stock.ConflictAsync(inactiveBaseUnit, "UNIT_IS_BASE_UNIT", "unitId");
        await AssertConversionsAsync(s, s.A, ("box", 12m));
    }

    [Fact]
    public async Task AC25_The_factor_of_a_conversion_whose_unit_was_deactivated_can_still_be_replaced()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetUnitActiveAsync(s.Http, s.BoxUnit, false);

        using var replace = await Units.PutAsync(s.Http, s.A, s.Box, 10);
        // R4: creating a conversion with that inactive unit on another article is still refused.
        using var create = await Units.PutAsync(s.Http, s.B, s.Box, 10);

        Assert.Equal(10m, (await HttpAssert.JsonAsync(replace, HttpStatusCode.OK)).Factor());
        await HttpAssert.ReferenceInactiveAsync(create, "unitId");
        await AssertConversionsAsync(s, s.A, ("box", 10m));
        await AssertConversionsAsync(s, s.B);
    }

    // ---- AC-26 ----

    [Fact]
    public async Task AC26_Delete_removes_the_conversion()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.A, s.Pack, 6);

        using var delete = await Units.DeleteAsync(s.Http, s.A, s.Pack);
        using var get = await Units.SendGetAsync(s.Http, s.A, s.Pack);
        using var again = await Units.DeleteAsync(s.Http, s.A, s.Pack);
        using var unknownArticle = await Units.DeleteAsync(s.Http, Guid.NewGuid(), s.Box);
        using var baseUnit = await Units.SendGetAsync(s.Http, s.A, s.Pcs);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await HttpAssert.NotFoundAsync(get);
        await HttpAssert.NotFoundAsync(again);
        await HttpAssert.NotFoundAsync(unknownArticle);
        // R5 / decision 2: the base unit has no conversion row.
        await HttpAssert.NotFoundAsync(baseUnit);
        await AssertConversionsAsync(s, s.A, ("box", 12m));
    }

    [Fact]
    public async Task AC26_R3_A_deleted_conversion_can_be_set_again_as_a_new_one()
    {
        var s = await Units.SetupAsync(app);
        await Units.RemoveAsync(s.Http, s.A, s.Box);

        using var response = await Units.PutAsync(s.Http, s.A, s.Box, 24);

        Assert.Equal(24m, (await HttpAssert.JsonAsync(response, HttpStatusCode.Created)).Factor());
        await AssertConversionsAsync(s, s.A, ("box", 24m));
    }

    // ---- AC-27, AC-28 ----

    [Fact]
    public async Task AC27_A_service_article_may_have_a_conversion_and_a_factor_may_be_one()
    {
        var s = await Units.SetupAsync(app);

        using var service = await Units.PutAsync(s.Http, s.Service, s.Box, 12);
        using var one = await Units.PutAsync(s.Http, s.A, s.Pack, 1);

        Assert.Equal(s.Service, (await HttpAssert.JsonAsync(service, HttpStatusCode.Created)).GetProperty("article").Id());
        Assert.Equal(1m, (await HttpAssert.JsonAsync(one, HttpStatusCode.Created)).Factor());
    }

    [Fact]
    public async Task AC28_The_same_unit_has_its_own_factor_on_each_article()
    {
        var s = await Units.SetupAsync(app);
        await Units.SetAsync(s.Http, s.B, s.Box, 50);

        var onA = await Units.GetAsync(s.Http, s.A, s.Box);
        var onB = await Units.GetAsync(s.Http, s.B, s.Box);
        // Replacing one does not touch the other.
        await Units.SetAsync(s.Http, s.B, s.Box, 48);

        Assert.Equal((s.A, 12m), (onA.GetProperty("article").Id(), onA.Factor()));
        Assert.Equal((s.B, 50m), (onB.GetProperty("article").Id(), onB.Factor()));
        Assert.Equal(12m, (await Units.GetAsync(s.Http, s.A, s.Box)).Factor());
        Assert.Equal(48m, (await Units.GetAsync(s.Http, s.B, s.Box)).Factor());
    }

    // ---- AC-29 ----

    [Fact]
    public async Task AC29_Articles_filter_by_alternative_unit()
    {
        var s = await Units.SetupAsync(app);
        // An article whose base unit is box, without a conversion for it, is not a match.
        var boxed = await Art.CreateAsync(s.Http, "C", "Boxed article", s.Box);
        await Units.SetAsync(s.Http, s.Service, s.Box, 4);
        await Units.SetAsync(s.Http, s.B, s.Pack, 6);

        var byBox = await Art.ListAsync(s.Http, $"?alternativeUnitId={s.Box}");
        var byPack = await Art.ListAsync(s.Http, $"?alternativeUnitId={s.Pack}");
        var combined = await Art.ListAsync(s.Http, $"?alternativeUnitId={s.Box}&type=stock");
        var byBase = await Art.ListAsync(s.Http, $"?alternativeUnitId={s.Pcs}");
        var random = await Art.ListAsync(s.Http, $"?alternativeUnitId={Guid.NewGuid()}");
        using var malformed = await s.Http.GetAsync($"{Art.Path}?alternativeUnitId=abc");

        Assert.Equal(new[] { "A", "S" }, byBox.Codes().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(2, byBox.Total());
        Assert.DoesNotContain(boxed.Str("code"), byBox.Codes());
        Assert.Equal(new[] { "B" }, byPack.Codes());
        Assert.Equal(new[] { "A" }, combined.Codes());
        Assert.Equal(0, byBase.Total());
        Assert.Equal(0, random.Total());
        Assert.Empty(random.Codes());
        await HttpAssert.ValidationAsync(malformed, "alternativeUnitId");
    }

    [Fact]
    public async Task AC29_The_article_representation_is_unchanged()
    {
        var s = await Units.SetupAsync(app);

        var article = await Art.GetAsync(s.Http, s.A);

        // Spec 002, section 4: the eleven properties of an Article; conversions are a sub-resource (ADR-0014).
        Assert.Equal(
            new[] { "baseUnit", "code", "createdAt", "createdBy", "description", "id", "isActive", "name", "type", "updatedAt", "updatedBy" },
            article.PropertyNames());
        Assert.Equal(s.Pcs, article.GetProperty("baseUnit").Id());
    }

    // ---- E3 ----

    [Fact]
    public async Task AC20_E3_Parallel_sets_of_the_same_new_conversion_leave_exactly_one()
    {
        var s = await Units.SetupAsync(app);
        decimal[] factors = [2m, 3m, 4m, 5m, 6m, 7m];

        var responses = await Task.WhenAll(factors.Select(f => Task.Run(() => Units.PutAsync(s.Http, s.A, s.Pack, f))));

        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();
        Assert.All(statuses, status => Assert.True(status is 200 or 201, $"Statuses: [{string.Join(", ", statuses)}]"));
        Assert.Contains(201, statuses);
        var list = await Units.ListAsync(s.Http, s.A);
        Assert.Equal(new[] { "box", "pack" }, list.UnitCodes());
        Assert.Equal(2, list.Total());
        Assert.Contains((await Units.GetAsync(s.Http, s.A, s.Pack)).Factor(), factors);
        foreach (var response in responses)
            response.Dispose();
    }

    // ---- R10 ----

    [Fact]
    public async Task AC20_R10_A_conversion_shows_the_current_code_and_name_of_its_unit_and_article()
    {
        // R10: a used unit "can still be renamed, re-coded and deactivated"; summaries are current (ADR-0008).
        var s = await Units.SetupAsync(app);

        using (var rename = await Units.PutUnitAsync(s.Http, s.Box, "BX", "Big box", false))
            await HttpAssert.JsonAsync(rename, HttpStatusCode.OK);
        await Stock.ReplaceArticleAsync(s.Http, s.A, Stock.ArticleBody(s.S.ArticleA).With("code", "A2").With("name", "Article two"));

        var conversion = await Units.GetAsync(s.Http, s.A, s.Box);
        Assert.Equal(("BX", "Big box"), (conversion.GetProperty("unit").Str("code"), conversion.GetProperty("unit").Str("name")));
        Assert.Equal(("A2", "Article two"), (conversion.GetProperty("article").Str("code"), conversion.GetProperty("article").Str("name")));
        Assert.Equal(12m, conversion.Factor());
    }
}
