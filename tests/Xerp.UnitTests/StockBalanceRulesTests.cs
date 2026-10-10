using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 011 / ADR-0018: the stored balance of an (article, warehouse) pair is the sum of the pair's ledger
/// entries (R19-R21) and is never negative. The arithmetic, without a database.
/// </summary>
public class StockBalanceRulesTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();

    [Fact]
    public void A_new_balance_is_zero_for_its_pair()
    {
        var balance = StockBalance.Start(Tenant, W1, A);

        Assert.Equal((Tenant, W1, A, 0m), (balance.TenantId, balance.WarehouseId, balance.ArticleId, balance.Quantity));
    }

    [Fact]
    public void R21_Each_entry_adds_its_signed_quantity()
    {
        var balance = StockBalance.Start(Tenant, W1, A);

        balance.Add(100m);
        balance.Add(-30.5m);
        balance.Add(0.000001m);

        Assert.Equal(69.500001m, balance.Quantity);

        balance.Add(-69.500001m);
        Assert.Equal(0m, balance.Quantity);
    }

    [Fact]
    public void R20_A_balance_never_goes_negative_and_a_refused_change_leaves_it_as_it_was()
    {
        var balance = StockBalance.Start(Tenant, W1, A);
        balance.Add(10m);

        Assert.Throws<InvalidOperationException>(() => balance.Add(-10.000001m));

        Assert.Equal(10m, balance.Quantity);
    }

    [Fact]
    public void R25_Rebuild_sets_a_balance_to_the_ledger_sum()
    {
        var balance = StockBalance.Start(Tenant, W1, A);
        balance.Add(12m);

        balance.CorrectTo(10m);

        Assert.Equal(10m, balance.Quantity);
        Assert.Throws<InvalidOperationException>(() => balance.CorrectTo(-1m));
        Assert.Equal(10m, balance.Quantity);
    }

    [Fact]
    public void R21_The_changes_of_a_posting_are_the_sums_of_its_movements_per_pair()
    {
        // A transfer of A (two lines) from W1 to W2 and a receipt of B: what one save writes to the ledger.
        StockMovement[] movements =
        [
            new(A, W1, -4m), new(A, W2, 4m),
            new(A, W1, -1.5m), new(A, W2, 1.5m),
            new(B, W1, 7m),
        ];

        var changes = StockBalanceRules.Changes(movements);

        Assert.Equal(3, changes.Count);
        Assert.Equal(-5.5m, changes[(A, W1)]);
        Assert.Equal(5.5m, changes[(A, W2)]);
        Assert.Equal(7m, changes[(B, W1)]);
    }

    [Fact]
    public void R21_A_pair_whose_movements_cancel_is_still_a_pair_and_no_movements_change_nothing()
    {
        var changes = StockBalanceRules.Changes([new StockMovement(A, W1, 3m), new StockMovement(A, W1, -3m)]);

        Assert.Equal(0m, Assert.Single(changes).Value);
        Assert.Empty(StockBalanceRules.Changes([]));
    }

    [Fact]
    public void R20_Posting_and_reversal_together_return_every_pair_to_where_it_was()
    {
        StockMovement[] posted = [new(A, W1, -4m), new(A, W2, 4m), new(B, W1, 7m)];
        var reversed = posted.Select(StockMovements.Opposite).ToArray();
        var balances = new Dictionary<(Guid, Guid), StockBalance>
        {
            [(A, W1)] = StockBalance.Start(Tenant, W1, A),
            [(A, W2)] = StockBalance.Start(Tenant, W2, A),
            [(B, W1)] = StockBalance.Start(Tenant, W1, B),
        };
        balances[(A, W1)].Add(10m);

        foreach (var (pair, change) in StockBalanceRules.Changes(posted))
            balances[pair].Add(change);
        Assert.Equal((6m, 4m, 7m), (balances[(A, W1)].Quantity, balances[(A, W2)].Quantity, balances[(B, W1)].Quantity));

        foreach (var (pair, change) in StockBalanceRules.Changes(reversed))
            balances[pair].Add(change);
        Assert.Equal((10m, 0m, 0m), (balances[(A, W1)].Quantity, balances[(A, W2)].Quantity, balances[(B, W1)].Quantity));
    }
}
