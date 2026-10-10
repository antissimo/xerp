using System.Net;
using System.Text.Json;
using Xerp.IntegrationTests.Support;

namespace Xerp.IntegrationTests;

/// <summary>
/// Spec 011, AC-55 to AC-58 (R19–R28): the stored balance equals the sum of the ledger under parallel
/// postings of every kind, and verify and rebuild called while postings run see and leave a healthy tenant.
/// </summary>
[Collection(XerpCollection.Name)]
public class StockBalanceRaceTests(XerpFixture app)
{
    /// <summary>One parallel request of a race: what it is, the document it posts or reverses, and how it is sent.</summary>
    private sealed record Shot(string Kind, Guid Document, Func<Task<HttpResponseMessage>> Send, string[] AllowedConflicts);

    private sealed record Outcome(Shot Shot, HttpStatusCode Status, JsonElement Body);

    /// <summary>Sends all shots at once and reads every response; none may be anything but success or an allowed 409.</summary>
    private static async Task<Outcome[]> FireAsync(IEnumerable<Shot> shots)
    {
        var outcomes = await Task.WhenAll(shots.Select(shot => Task.Run(async () =>
        {
            using var response = await shot.Send();
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"{shot.Kind}: unexpected status {(int)response.StatusCode}: {text}");
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                using var parsed = JsonDocument.Parse(text);
                var code = parsed.RootElement.Str("code");
                Assert.True(shot.AllowedConflicts.Contains(code),
                    $"{shot.Kind}: 409 {code} is not a code this type allows [{string.Join(", ", shot.AllowedConflicts)}]: {text}");
                return new Outcome(shot, response.StatusCode, await HttpAssert.ProblemAsync(response, HttpStatusCode.Conflict, code));
            }
            using var document = JsonDocument.Parse(text);
            return new Outcome(shot, response.StatusCode, document.RootElement.Clone());
        })));
        return outcomes;
    }

    private static Shot Post(HttpClient http, string kind, Guid draft, params string[] allowed) =>
        new(kind, draft, () => Stock.SendPostAsync(http, draft), allowed);

    /// <summary>The numbers of the posted and reversed documents of a type are 1..n without a gap.</summary>
    private static async Task AssertGaplessAsync(HttpClient http, string type)
    {
        var numbers = (await Balance.AllAsync(http, $"{Stock.Documents}?type={type}"))
            .Where(d => d.Str("status") != "draft").Select(d => d.Number()!).ToArray();
        Assert.NotEmpty(numbers);
        var ordinals = numbers.Select(n => int.Parse(n[(n.LastIndexOf('-') + 1)..])).Order().ToArray();
        Assert.True(Enumerable.Range(1, numbers.Length).SequenceEqual(ordinals),
            $"The numbers of the posted {type} documents have a gap or a repeat: [{string.Join(", ", numbers.Order(StringComparer.Ordinal))}]");
        Assert.Single(numbers.Select(n => n[..(n.LastIndexOf('-') + 1)]).Distinct());
        var drafts = (await Balance.AllAsync(http, $"{Stock.Documents}?type={type}&status=draft"));
        Assert.All(drafts, d => Assert.Null(d.Number()));
    }

    // ---- AC-55 ----

    [Fact]
    public async Task AC55_Parallel_issues_leave_the_balance_equal_to_the_ledger()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 10);
        var shots = new List<Shot>();
        for (var i = 0; i < 10; i++)
            shots.Add(Post(s.Http, "issue", (await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 3))).Id(), "INSUFFICIENT_STOCK"));

        var outcomes = await FireAsync(shots);

        Assert.Equal(3, outcomes.Count(o => o.Status == HttpStatusCode.OK));
        Assert.Equal(7, outcomes.Count(o => o.Status == HttpStatusCode.Conflict));
        Assert.Equal(1m, await Balance.ListedQuantityAsync(s.Http, s.A, s.W1));
        var balanced = await Balance.AssertBalancedAsync(s, "after ten parallel issues");
        Assert.Equal(1m, balanced[(s.A, s.W1)]);
        Assert.Equal((1, 0), await Balance.RebuildAsync(s.Http));
        await AssertGaplessAsync(s.Http, "issue");
    }

    [Fact]
    public async Task AC55_Parallel_issues_of_several_articles_in_opposite_order_stay_balanced()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.PostDocumentAsync(s.Http, (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 10), (s.B, 10))).Id());
        var shots = new List<Shot>();
        for (var i = 0; i < 10; i++)
        {
            var (first, second) = i % 2 == 0 ? (s.A, s.B) : (s.B, s.A);
            shots.Add(Post(s.Http, "issue", (await Stock.CreateAsync(s.Http, "issue", s.W1, (first, 4), (second, 4))).Id(), "INSUFFICIENT_STOCK"));
        }

        var outcomes = await FireAsync(shots);

        Assert.Equal(2, outcomes.Count(o => o.Status == HttpStatusCode.OK));
        var balanced = await Balance.AssertBalancedAsync(s, "after parallel issues of two articles");
        Assert.Equal(2m, balanced[(s.A, s.W1)]);
        Assert.Equal(2m, balanced[(s.B, s.W1)]);
    }

    // ---- AC-56 ----

    [Fact]
    public async Task AC56_Mixed_parallel_postings_leave_every_balance_equal_to_the_ledger()
    {
        var s = await Stock.SetupAsync(app);
        var http = s.Http;
        string[] insufficient = ["INSUFFICIENT_STOCK"];

        // 1000 of A and of B in W1; four of the receipts that brought A are reversed during the race.
        await Stock.PostDocumentAsync(http, (await Stock.CreateAsync(http, "receipt", s.W1, (s.A, 960), (s.B, 1000))).Id());
        var earlier = new List<Guid>();
        for (var i = 0; i < 4; i++)
            earlier.Add((await Stock.ReceiveAsync(http, s.W1, s.A, 10)).Id());
        Assert.Equal(1000m, await Balance.ListedQuantityAsync(http, s.A, s.W1));
        Assert.Equal(1000m, await Balance.ListedQuantityAsync(http, s.B, s.W1));
        var baseEntries = await Balance.LedgerAsync(http);

        var shots = new List<Shot>();
        for (var i = 0; i < 6; i++)
            shots.Add(Post(http, "receipt into W1", (await Stock.CreateAsync(http, "receipt", s.W1, (s.A, 7))).Id()));
        for (var i = 0; i < 4; i++)
            shots.Add(Post(http, "receipt into W2", (await Stock.CreateAsync(http, "receipt", s.W2, (s.A, 5), (s.B, 3))).Id()));
        for (var i = 0; i < 6; i++)
            shots.Add(Post(http, "issue from W1", (await Stock.CreateAsync(http, "issue", s.W1, (s.A, 11), (s.B, 2))).Id(), insufficient));
        for (var i = 0; i < 5; i++)
            shots.Add(Post(http, "transfer W1 -> W2", (await Stock.CreateTransferAsync(http, s.W1, s.W2, (s.A, 20))).Id(), insufficient));
        for (var i = 0; i < 5; i++)
            shots.Add(Post(http, "transfer W2 -> W1", (await Stock.CreateTransferAsync(http, s.W2, s.W1, (s.A, 15))).Id(), insufficient));
        foreach (var counted in new[] { 990m, 995m, 1005m, 1000m })
            shots.Add(Post(http, "count of B in W1", (await Counts.CountAsync(http, s.W1, s.B, counted)).Id(), "COUNT_OUTDATED"));
        foreach (var receipt in earlier)
            shots.Add(new Shot("reversal of a receipt", receipt, () => Stock.SendReverseAsync(http, receipt), ["INSUFFICIENT_STOCK", "INVALID_STATE"]));
        Assert.True(shots.Count >= 30);

        var outcomes = await FireAsync(shots.OrderBy(_ => Random.Shared.Next()).ToArray());

        // Receipts need nothing and never conflict; a reversal answers 201, a posting 200.
        Assert.All(outcomes.Where(o => o.Shot.Kind.StartsWith("receipt")), o => Assert.Equal(HttpStatusCode.OK, o.Status));
        Assert.All(outcomes.Where(o => o.Status != HttpStatusCode.Conflict), o =>
            Assert.Equal(o.Shot.Kind.StartsWith("reversal") ? HttpStatusCode.Created : HttpStatusCode.OK, o.Status));

        var balanced = await Balance.AssertBalancedAsync(s, "after the mixed race");

        // What the succeeded documents wrote, and nothing else, is in the ledger and in the balances.
        var expected = Balance.Sums(baseEntries);
        var written = baseEntries.Length;
        foreach (var outcome in outcomes)
        {
            // For a reversal the response is the reversing document; its entries are what the reversal wrote.
            var entries = outcome.Status == HttpStatusCode.Conflict ? [] : await Stock.EntriesAsync(http, outcome.Body.Id());
            if (outcome.Status == HttpStatusCode.Conflict && !outcome.Shot.Kind.StartsWith("reversal"))
            {
                Assert.Empty(await Stock.EntriesAsync(http, outcome.Shot.Document));
                await Stock.AssertDraftAsync(http, outcome.Shot.Document);
            }
            if (outcome.Status != HttpStatusCode.Conflict && !outcome.Shot.Kind.StartsWith("count"))
                Assert.NotEmpty(entries);
            written += entries.Length;
            foreach (var entry in entries)
            {
                var pair = (entry.GetProperty("article").Id(), entry.GetProperty("warehouse").Id());
                expected[pair] = expected.GetValueOrDefault(pair) + entry.Quantity();
            }
        }
        Assert.Equal(written, (await Stock.LedgerAsync(http)).Total());
        foreach (var article in new[] { s.A, s.B })
            foreach (var warehouse in new[] { s.W1, s.W2 })
            {
                var sum = expected.GetValueOrDefault((article, warehouse));
                Assert.True(balanced[(article, warehouse)] == sum,
                    $"Listed quantity {balanced[(article, warehouse)]} of ({article}, {warehouse}) is not the sum {sum} of the entries of the succeeded documents.");
                Assert.Equal(sum, await Balance.ListedQuantityAsync(http, article, warehouse));
            }

        // The goods are all somewhere: what came in minus what went out, over both warehouses.
        var totalA = baseEntries.Where(e => e.GetProperty("article").Id() == s.A).Sum(e => e.Quantity())
            + outcomes.Count(o => o.Shot.Kind == "receipt into W1") * 7m + outcomes.Count(o => o.Shot.Kind == "receipt into W2") * 5m
            - outcomes.Count(o => o.Shot.Kind == "issue from W1" && o.Status == HttpStatusCode.OK) * 11m
            - outcomes.Count(o => o.Shot.Kind.StartsWith("reversal") && o.Status == HttpStatusCode.Created) * 10m;
        Assert.Equal(totalA, balanced[(s.A, s.W1)] + balanced[(s.A, s.W2)]);

        var rebuilt = await Balance.RebuildAsync(http);
        Assert.Equal(0, rebuilt.Corrected);
        Assert.Equal(Balance.Sums(await Balance.LedgerAsync(http)).Count, rebuilt.Pairs);
        foreach (var type in new[] { "receipt", "issue", "transfer" })
            await AssertGaplessAsync(http, type);
    }

    // ---- AC-57 ----

    /// <summary>30 drafts on (A, W1): 15 receipts of 2 and 15 issues of 3, in turn.</summary>
    private static async Task<List<Shot>> ReceiptsAndIssuesAsync(StockSetup s)
    {
        var shots = new List<Shot>();
        for (var i = 0; i < 15; i++)
        {
            shots.Add(Post(s.Http, "receipt", (await Stock.CreateAsync(s.Http, "receipt", s.W1, (s.A, 2))).Id()));
            shots.Add(Post(s.Http, "issue", (await Stock.CreateAsync(s.Http, "issue", s.W1, (s.A, 3))).Id(), "INSUFFICIENT_STOCK"));
        }
        return shots;
    }

    [Fact]
    public async Task AC57_Verify_called_during_parallel_postings_never_reports_a_difference()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 20);
        var shots = await ReceiptsAndIssuesAsync(s);
        var postings = FireAsync(shots);
        var calls = 0;

        // Four callers keep asking until the postings are done, and at least 30 times in all.
        async Task VerifyAsync()
        {
            while (!postings.IsCompleted || Volatile.Read(ref calls) < 30)
            {
                using var response = await s.Http.GetAsync(Balance.Differences);
                var body = await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
                Assert.True(body.Total() == 0 && body.Items().Length == 0, $"Verify saw a difference while postings ran: {body}");
                Interlocked.Increment(ref calls);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(VerifyAsync)));
        var outcomes = await postings;

        Assert.True(calls >= 30, $"Verify was called {calls} times.");
        Assert.Equal(30, outcomes.Length);
        Assert.All(outcomes.Where(o => o.Shot.Kind == "receipt"), o => Assert.Equal(HttpStatusCode.OK, o.Status));
        var issued = outcomes.Count(o => o.Shot.Kind == "issue" && o.Status == HttpStatusCode.OK);
        var balanced = await Balance.AssertBalancedAsync(s, "after the postings");
        Assert.Equal(20m + 15 * 2 - issued * 3, balanced[(s.A, s.W1)]);
    }

    // ---- AC-58 ----

    [Fact]
    public async Task AC58_Rebuild_called_during_parallel_postings_corrects_nothing_and_loses_nothing()
    {
        var s = await Stock.SetupAsync(app);
        await Stock.ReceiveAsync(s.Http, s.W1, s.A, 20);
        var shots = await ReceiptsAndIssuesAsync(s);
        var postings = FireAsync(shots);
        var calls = 0;

        // Two callers keep rebuilding until the postings are done, and at least five times in all.
        async Task RebuildAsync()
        {
            while (!postings.IsCompleted || Volatile.Read(ref calls) < 5)
            {
                var (pairs, corrected) = await Balance.RebuildAsync(s.Http);
                Assert.True(corrected == 0, $"Rebuild corrected {corrected} balances while postings ran.");
                Assert.Equal(1, pairs);
                Interlocked.Increment(ref calls);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(RebuildAsync)));
        var outcomes = await postings;

        Assert.True(calls >= 5, $"Rebuild was called {calls} times.");
        Assert.All(outcomes.Where(o => o.Shot.Kind == "receipt"), o => Assert.Equal(HttpStatusCode.OK, o.Status));
        Assert.All(outcomes.Where(o => o.Status == HttpStatusCode.Conflict), o => Assert.Equal("INSUFFICIENT_STOCK", o.Body.Str("code")));
        var issued = outcomes.Count(o => o.Shot.Kind == "issue" && o.Status == HttpStatusCode.OK);
        var balanced = await Balance.AssertBalancedAsync(s, "after the postings and the rebuilds");
        Assert.Equal(20m + 15 * 2 - issued * 3, balanced[(s.A, s.W1)]);
        Assert.Equal(16 + issued, (await Stock.LedgerAsync(s.Http)).Total());
        await AssertGaplessAsync(s.Http, "receipt");
        if (issued > 0)
            await AssertGaplessAsync(s.Http, "issue");
        Assert.Equal((1, 0), await Balance.RebuildAsync(s.Http));
        Assert.Equal(0, await Balance.DifferencesAsync(s.Http));
    }
}
