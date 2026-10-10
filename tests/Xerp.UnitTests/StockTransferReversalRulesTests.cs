using Xerp.Domain.Inventory;

namespace Xerp.UnitTests;

/// <summary>
/// Spec 006, AC-03: the sign rules of a transfer line (R6) and of a reversal (R14), with what follows from
/// them - conservation (R8), net zero (R15), no negative stock (R16), whole and final (R11, R18) - as Domain
/// rules, without HTTP or a database.
/// </summary>
public class StockTransferReversalRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddHours(3);
    private static readonly DateOnly Day = new(2026, 10, 9);
    private static readonly Guid Poster = Guid.CreateVersion7();
    private static readonly Guid Reverser = Guid.CreateVersion7();
    private static readonly Guid W1 = Guid.CreateVersion7();
    private static readonly Guid W2 = Guid.CreateVersion7();
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();

    // Spec 007: every line here is in the base unit of its article, as every line was before that spec.
    private static readonly Guid Pcs = Guid.CreateVersion7();

    private static List<StockLineEntry> Lines(params (Guid, decimal)[] lines) =>
        lines.Select(l => new StockLineEntry(l.Item1, Pcs, l.Item2)).ToList();

    private static StockDocument Draft(StockDocumentType type, params (Guid, decimal)[] lines) =>
        StockDocument.Create(type, Day, W1, type == StockDocumentType.Transfer ? W2 : null, "DN-7", "first note", Lines(lines), T0, Poster);

    private static (StockDocument Document, IReadOnlyList<StockLedgerEntry> Entries) Posted(StockDocumentType type, params (Guid, decimal)[] lines)
    {
        var document = Draft(type, lines);
        var number = DocumentNumber.Format(type, 1);
        return (document, document.PostInBaseUnits(number, T0, Poster));
    }

    private static Dictionary<(Guid, Guid), decimal> Sums(IEnumerable<StockLedgerEntry> entries) =>
        entries.GroupBy(e => (e.ArticleId, e.WarehouseId)).ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity));

    // ---- transfer: the document

    [Fact]
    public void R9_Transfers_are_numbered_ST()
    {
        Assert.Equal("ST-000001", DocumentNumber.Format(StockDocumentType.Transfer, 1));
        Assert.Equal("transfer", StockDocumentType.Transfer.ToName());
        Assert.True(StockDocumentTypeNames.TryParse("transfer", out var type));
        Assert.Equal(StockDocumentType.Transfer, type);
        Assert.False(StockDocumentTypeNames.TryParse("Transfer", out _));
    }

    [Fact]
    public void R2_R3_A_transfer_has_a_destination_other_than_its_source_and_other_types_have_none()
    {
        Assert.True(TransferRules.IsValidDestination(StockDocumentType.Transfer, W1, W2));
        Assert.False(TransferRules.IsValidDestination(StockDocumentType.Transfer, W1, null));
        Assert.False(TransferRules.IsValidDestination(StockDocumentType.Transfer, W1, W1));
        Assert.True(TransferRules.IsValidDestination(StockDocumentType.Receipt, W1, null));
        Assert.True(TransferRules.IsValidDestination(StockDocumentType.Issue, W1, null));
        Assert.False(TransferRules.IsValidDestination(StockDocumentType.Receipt, W1, W2));
        Assert.False(TransferRules.IsValidDestination(StockDocumentType.Issue, W1, W2));

        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Transfer, Day, W1, null, null, null, Lines((A, 1)), T0, Poster));
        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Transfer, Day, W1, W1, null, null, Lines((A, 1)), T0, Poster));
        Assert.Throws<ArgumentException>(() => StockDocument.Create(StockDocumentType.Receipt, Day, W1, W2, null, null, Lines((A, 1)), T0, Poster));
    }

    [Fact]
    public void E3_A_draft_transfer_may_swap_its_warehouses_and_a_rejected_replace_changes_nothing()
    {
        var transfer = Draft(StockDocumentType.Transfer, (A, 30));

        transfer.Replace(Day, W2, W1, null, null, Lines((B, 4)), T1, Reverser);

        Assert.Equal((W2, (Guid?)W1), (transfer.WarehouseId, transfer.ToWarehouseId));
        Assert.Throws<ArgumentException>(() => transfer.Replace(Day, W1, W1, null, null, Lines((A, 1)), T1, Reverser));
        Assert.Throws<ArgumentException>(() => transfer.Replace(Day, W1, null, null, null, Lines((A, 1)), T1, Reverser));
        Assert.Equal((W2, (Guid?)W1, B), (transfer.WarehouseId, transfer.ToWarehouseId, transfer.Lines.Single().ArticleId));
    }

    // ---- transfer: the sign rule (R6) and conservation (R8)

    [Fact]
    public void R6_The_movements_of_a_line_by_type()
    {
        var line = new StockLineValues(A, 30);

        Assert.Equal([new StockMovement(A, W1, 30)], StockMovements.OfLine(StockDocumentType.Receipt, W1, null, line));
        Assert.Equal([new StockMovement(A, W1, -30)], StockMovements.OfLine(StockDocumentType.Issue, W1, null, line));
        Assert.Equal(
            [new StockMovement(A, W1, -30), new StockMovement(A, W2, 30)],
            StockMovements.OfLine(StockDocumentType.Transfer, W1, W2, line));
        Assert.Throws<InvalidOperationException>(() => StockMovements.OfLine(StockDocumentType.Transfer, W1, null, line));
        Assert.Throws<InvalidOperationException>(() => StockMovements.OfLine(StockDocumentType.Transfer, W1, W1, line));
    }

    [Fact]
    public void R6_A_posted_transfer_line_writes_the_outgoing_entry_in_the_source_then_the_incoming_in_the_destination()
    {
        var (transfer, entries) = Posted(StockDocumentType.Transfer, (A, 30));

        Assert.Equal(StockDocumentStatus.Posted, transfer.Status);
        Assert.Equal(
            [(A, W1, -30m, 1), (A, W2, 30m, 1)],
            entries.Select(e => (e.ArticleId, e.WarehouseId, e.Quantity, e.LineNo)));
        Assert.All(entries, e => Assert.Equal((transfer.Id, Day, T0, Poster), (e.DocumentId, e.DocumentDate, e.PostedAt, e.PostedBy)));
    }

    [Fact]
    public void R8_Conservation_The_entries_of_a_transfer_sum_to_zero_per_article()
    {
        var (_, entries) = Posted(StockDocumentType.Transfer, (A, 10), (B, 5), (A, 2.5m), (B, 0.000001m));

        Assert.Equal(8, entries.Count);
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], entries.Select(e => e.LineNo));
        Assert.All(entries.GroupBy(e => e.ArticleId), g => Assert.Equal(0m, g.Sum(e => e.Quantity)));
        Assert.Equal(-12.5m, entries.Where(e => e.ArticleId == A && e.WarehouseId == W1).Sum(e => e.Quantity));
        Assert.Equal(12.5m, entries.Where(e => e.ArticleId == A && e.WarehouseId == W2).Sum(e => e.Quantity));
        // Nothing moves anywhere but in the two warehouses of the document.
        Assert.All(entries, e => Assert.Contains(e.WarehouseId, new[] { W1, W2 }));
    }

    // ---- reversal: the sign rule (R14) and net zero (R15)

    [Theory]
    [InlineData(StockDocumentType.Receipt, "SR-000002")]
    [InlineData(StockDocumentType.Issue, "SI-000002")]
    [InlineData(StockDocumentType.Transfer, "ST-000002")]
    public void R13_R14_R15_A_reversal_is_the_same_document_posted_with_the_opposite_entries(StockDocumentType type, string number)
    {
        var (original, entries) = Posted(type, (A, 10), (B, 5), (A, 2.5m));
        var before = (original.Number, original.DocumentDate, original.PostedAt, original.PostedBy, original.UpdatedAt, original.UpdatedBy, original.Note);
        var later = Day.AddDays(1);

        var (reversal, reversing) = original.Reverse(entries, later, "wrong article", number, T1, Reverser);

        // The reversing document (R13).
        Assert.NotEqual(original.Id, reversal.Id);
        Assert.Equal((type, StockDocumentStatus.Posted, number, later), (reversal.Type, reversal.Status, reversal.Number, reversal.DocumentDate));
        Assert.Equal((original.WarehouseId, original.ToWarehouseId), (reversal.WarehouseId, reversal.ToWarehouseId));
        Assert.Equal(("DN-7", "wrong article"), (reversal.Reference, reversal.Note));
        Assert.Equal((original.Id, (Guid?)null), (reversal.ReversalOfId, reversal.ReversedById));
        Assert.Equal((T1, T1, T1, Reverser, Reverser, Reverser),
            (reversal.CreatedAt, reversal.UpdatedAt, reversal.PostedAt!.Value, reversal.CreatedBy, reversal.UpdatedBy, reversal.PostedBy!.Value));
        Assert.Equal(original.Lines.Select(l => (l.LineNo, l.ArticleId, l.Quantity)), reversal.Lines.Select(l => (l.LineNo, l.ArticleId, l.Quantity)));
        Assert.All(reversal.Lines, l => Assert.Equal(reversal.Id, l.DocumentId));
        Assert.Empty(reversal.Lines.Select(l => l.Id).Intersect(original.Lines.Select(l => l.Id)));
        Assert.True(reversal.IsReversal);

        // The original: status and link, nothing else (R13).
        Assert.Equal((StockDocumentStatus.Reversed, (Guid?)reversal.Id, (Guid?)null), (original.Status, original.ReversedById, original.ReversalOfId));
        Assert.Equal(before, (original.Number, original.DocumentDate, original.PostedAt, original.PostedBy, original.UpdatedAt, original.UpdatedBy, original.Note));

        // The entries (R14): one per original entry, same article, warehouse and line, opposite quantity.
        Assert.Equal(entries.Count, reversing.Count);
        Assert.Equal(
            entries.Select(e => (e.ArticleId, e.WarehouseId, e.LineNo, Quantity: -e.Quantity)).Order(),
            reversing.Select(e => (e.ArticleId, e.WarehouseId, e.LineNo, e.Quantity)).Order());
        Assert.All(reversing, e => Assert.Equal((reversal.Id, later, T1, Reverser), (e.DocumentId, e.DocumentDate, e.PostedAt, e.PostedBy)));
        Assert.Empty(reversing.Select(e => e.Id).Intersect(entries.Select(e => e.Id)));
        // The original's entries are not touched.
        Assert.All(entries, e => Assert.Equal((original.Id, Day, T0, Poster), (e.DocumentId, e.DocumentDate, e.PostedAt, e.PostedBy)));

        // Net zero (R15): the pair sums to zero for every (article, warehouse).
        Assert.All(Sums(entries.Concat(reversing)).Values, sum => Assert.Equal(0m, sum));
    }

    [Fact]
    public void R14_The_signs_of_each_reversal()
    {
        var (receipt, received) = Posted(StockDocumentType.Receipt, (A, 100));
        var (issue, issued) = Posted(StockDocumentType.Issue, (A, 40));
        var (transfer, moved) = Posted(StockDocumentType.Transfer, (A, 30));

        var receiptBack = receipt.Reverse(received, Day, null, "SR-000002", T1, Reverser).Entries;
        var issueBack = issue.Reverse(issued, Day, null, "SI-000002", T1, Reverser).Entries;
        var transferBack = transfer.Reverse(moved, Day, null, "ST-000002", T1, Reverser).Entries;

        Assert.Equal([(W1, -100m)], receiptBack.Select(e => (e.WarehouseId, e.Quantity)));
        Assert.Equal([(W1, 40m)], issueBack.Select(e => (e.WarehouseId, e.Quantity)));
        Assert.Equal([(W2, -30m), (W1, 30m)], transferBack.Select(e => (e.WarehouseId, e.Quantity)));
        Assert.Equal(new StockMovement(A, W2, -30), StockMovements.Opposite(new StockMovement(A, W2, 30)));
    }

    [Fact]
    public void R13_A_reversal_without_a_note_has_none_and_keeps_the_reference()
    {
        var (original, entries) = Posted(StockDocumentType.Receipt, (A, 1));

        var reversal = original.Reverse(entries, Day, "   ", "SR-000002", T1, Reverser).Reversal;

        Assert.Null(reversal.Note);
        Assert.Equal("DN-7", reversal.Reference);
        Assert.Equal("first note", original.Note);
    }

    // ---- reversal: whole and final (R11, R12, R18)

    [Fact]
    public void R11_Only_a_posted_document_that_is_not_a_reversal_can_be_reversed()
    {
        var draft = Draft(StockDocumentType.Receipt, (A, 1));
        var (original, entries) = Posted(StockDocumentType.Receipt, (A, 1));
        Assert.False(draft.CanBeReversed);
        Assert.True(original.CanBeReversed);

        var (reversal, reversing) = original.Reverse(entries, Day, null, "SR-000002", T1, Reverser);

        Assert.False(original.CanBeReversed);
        Assert.False(reversal.CanBeReversed);
        Assert.Throws<InvalidOperationException>(() => draft.Reverse([], Day, null, "SR-000009", T1, Reverser));
        Assert.Throws<InvalidOperationException>(() => original.Reverse(entries, Day, null, "SR-000003", T1, Reverser));
        Assert.Throws<InvalidOperationException>(() => reversal.Reverse(reversing, Day, null, "SR-000003", T1, Reverser));
        Assert.Equal(reversal.Id, original.ReversedById);
    }

    [Fact]
    public void R12_A_reversal_is_not_dated_before_its_original_and_a_refused_reversal_changes_nothing()
    {
        var (original, entries) = Posted(StockDocumentType.Receipt, (A, 1));
        var (other, otherEntries) = Posted(StockDocumentType.Receipt, (A, 1));

        Assert.Throws<ArgumentException>(() => original.Reverse(entries, Day.AddDays(-1), null, "SR-000002", T1, Reverser));
        Assert.Throws<ArgumentException>(() => original.Reverse(entries, Day, new string('n', 2001), "SR-000002", T1, Reverser));
        Assert.Throws<ArgumentException>(() => original.Reverse(entries, Day, null, " ", T1, Reverser));
        // Reversing entries come from this document's entries only, all of them or nothing.
        Assert.Throws<ArgumentException>(() => original.Reverse([], Day, null, "SR-000002", T1, Reverser));
        Assert.Throws<ArgumentException>(() => original.Reverse(otherEntries, Day, null, "SR-000002", T1, Reverser));
        Assert.Equal((StockDocumentStatus.Posted, (Guid?)null), (original.Status, original.ReversedById));
        Assert.Equal(StockDocumentStatus.Posted, other.Status);

        Assert.Equal(Day, original.Reverse(entries, Day, null, "SR-000002", T1, Reverser).Reversal.DocumentDate);
    }

    [Fact]
    public void R18_Reversed_and_reversing_documents_are_immutable()
    {
        var (original, entries) = Posted(StockDocumentType.Transfer, (A, 1));
        var reversal = original.Reverse(entries, Day, null, "ST-000002", T1, Reverser).Reversal;

        foreach (var document in new[] { original, reversal })
        {
            Assert.False(document.IsDraft);
            Assert.Throws<InvalidOperationException>(() => document.Replace(Day, W1, W2, null, null, Lines((A, 2)), T1, Reverser));
            Assert.Throws<InvalidOperationException>(() => document.PostInBaseUnits("ST-000003", T1, Reverser));
        }
        Assert.Equal("reversed", original.Status.ToName());
        Assert.Equal("posted", reversal.Status.ToName());
        Assert.True(StockDocumentStatusNames.TryParse("reversed", out var status));
        Assert.Equal(StockDocumentStatus.Reversed, status);
    }

    // ---- no negative stock by reversal (R16)

    /// <summary>The articles of the pairs that go below zero where the tenant does not allow negative stock (the default).</summary>
    private static Guid[] ShortArticles(StockMovement[] movements, Dictionary<(Guid, Guid), decimal> onHand) =>
        StockMovements.ShortPairs(movements, onHand, negativeStockAllowed: false).Select(p => p.ArticleId).ToArray();

    [Fact]
    public void R16_A_pair_that_would_go_below_zero_makes_its_article_short()
    {
        var onHand = new Dictionary<(Guid, Guid), decimal> { [(A, W1)] = 60, [(B, W1)] = 5, [(A, W2)] = 20 };

        // Reversing a receipt of 100 A after 40 left: short. Of 5 B with 5 on hand: covered.
        Assert.Equal([A], ShortArticles([new(A, W1, -100), new(B, W1, -5)], onHand));
        // Exactly what is on hand may go.
        Assert.Empty(ShortArticles([new(A, W1, -60)], onHand));
        Assert.Equal([A], ShortArticles([new(A, W1, -60.000001m)], onHand));
        // Several movements of one pair count together.
        Assert.Equal([A], ShortArticles([new(A, W1, -40), new(A, W1, -40)], onHand));
        // Reversing an issue only adds: always covered, also for a pair without stock.
        Assert.Empty(ShortArticles([new(A, W1, 40), new(B, W2, 1)], onHand));
        // Reversing a transfer W1 -> W2 needs the goods in the destination; plenty in the source does not help.
        Assert.Equal([A], ShortArticles([new(A, W1, 30), new(A, W2, -30)], onHand));
        Assert.Empty(ShortArticles([new(A, W1, 20), new(A, W2, -20)], onHand));
        // A pair that has no stock is at zero.
        Assert.Equal([B], ShortArticles([new(B, W2, -0.000001m)], onHand));
    }
}
