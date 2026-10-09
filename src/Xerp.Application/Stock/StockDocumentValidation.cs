using System.Globalization;
using Xerp.Application.Common;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Stock;

/// <summary>
/// Input rules for stock documents and the stock queries (spec 005, R1-R7, R20-R22). No I/O. Every invalid
/// field and line is reported together (R8); errors about a line are keyed by its position in the request,
/// <c>lines[0].quantity</c> (ADR-0012, decision 12).
/// </summary>
public static class StockDocumentValidation
{
    private const string DateFormat = "yyyy-MM-dd";

    private const string TypeMessage =
        $"type must be \"{StockDocumentTypeNames.Receipt}\", \"{StockDocumentTypeNames.Issue}\", \"{StockDocumentTypeNames.Transfer}\" or \"{StockDocumentTypeNames.Count}\".";

    private const string StatusMessage =
        $"status must be \"{StockDocumentStatusNames.Draft}\", \"{StockDocumentStatusNames.Posted}\" or \"{StockDocumentStatusNames.Reversed}\".";

    public const string ToWarehouseField = "toWarehouseId";
    public const string UnitField = "unitId";
    public const string PurchaseOrderField = OrderLinkChecks.PurchaseOrderField;
    private const string OrderLineField = StockLineChecks.OrderLineField;

    public static string LineKey(int index, string field) => $"lines[{index}].{field}";

    public static Result<NewStockDocumentValues> Create(CreateStockDocumentInput input)
    {
        var errors = new ValidationErrors();
        if (!StockDocumentTypeNames.TryParse(input.Type, out var type))
            errors.Add("type", string.IsNullOrEmpty(input.Type) ? "type is required. " + TypeMessage : TypeMessage);
        // Spec 009, R18: the type decides about purchaseOrderId only. A document that sends one is judged as
        // linked, whatever its type, so a valid orderLineNo on its lines is not reported with it.
        var linked = input.PurchaseOrderId is not null;
        Guid? purchaseOrderId = null;
        if (linked)
        {
            if (!errors.Has("type") && type != StockDocumentType.Receipt)
                errors.Add(PurchaseOrderField,
                    $"purchaseOrderId is only for a receipt: goods of a purchase order are received with a receipt. For {(type == StockDocumentType.Issue ? "an" : "a")} {type.ToName()} leave it out or pass null.");
            else if (Guid.TryParse(input.PurchaseOrderId, out var parsed))
                purchaseOrderId = parsed;
            else
                errors.Add(PurchaseOrderField, "purchaseOrderId must be the id (UUID) of a purchase order, not its number.");
        }
        // An unknown type is judged like a receipt: a quantity greater than 0, repeats allowed.
        var values = Values(errors, type, linked, input.DocumentDate, input.WarehouseId, input.ToWarehouseId, input.Reference, input.Note, input.Lines);
        // Only for a known type can the destination be judged (spec 006, R2, R3).
        if (!errors.Has("type") && !errors.Has(ToWarehouseField)
            && DestinationMessage(type, values.WarehouseId, values.ToWarehouseId) is { } message)
            errors.Add(ToWarehouseField, message);
        if (errors.Any)
            return errors.ToError();
        return new NewStockDocumentValues(type, values, purchaseOrderId);
    }

    public static Result<StockDocumentValues> Replace(ReplaceStockDocumentInput input)
    {
        var errors = new ValidationErrors();
        foreach (var property in new[] { nameof(input.Reference), nameof(input.Note) })
        {
            if (!input.Has(property))
            {
                var field = FieldRules.FieldName(property);
                errors.Add(field, $"{field} is required (it may be null).");
            }
        }
        // The body has neither a type nor a link, so what depends on them (spec 008, R3, R4; spec 009, R20)
        // waits for the stored document: OfType.
        var values = Values(errors, type: null, linked: null, input.DocumentDate, input.WarehouseId, input.ToWarehouseId, input.Reference, input.Note, input.Lines);
        if (errors.Any)
            return errors.ToError();
        return values;
    }

    /// <summary>
    /// The rules of form that depend on the type, for a replace, whose body has none (spec 006, R4; spec 008,
    /// R3, R4, E10): null when the validated values suit a document of <paramref name="type"/>, otherwise
    /// <c>VALIDATION_FAILED</c> with every such key together - <c>toWarehouseId</c>, <c>lines[i].quantity</c>
    /// for a zero on a document that is not a count, <c>lines[i].articleId</c> for a repeated article on a count.
    /// The link to an order is decided by the stored document too (spec 009, R19, R20): <c>lines[i].orderLineNo</c>
    /// for a line without one on a linked document and for a line with one on an unlinked document.
    /// </summary>
    /// <param name="linked">Whether the stored document is linked to an order.</param>
    public static AppError? OfType(StockDocumentType type, StockDocumentValues values, bool linked = false)
    {
        var errors = new ValidationErrors();
        if (DestinationMessage(type, values.WarehouseId, values.ToWarehouseId) is { } message)
            errors.Add(ToWarehouseField, message);
        for (var i = 0; i < values.Lines.Count; i++)
        {
            if (!QuantityRules.IsValidOn(type, values.Lines[i].Quantity))
                errors.Add(LineKey(i, "quantity"), QuantityMessage(type));
            if (OrderLineMessage(linked, values.Lines[i].OrderLineNo) is { } orderLineMessage)
                errors.Add(LineKey(i, OrderLineField), orderLineMessage);
        }
        Repeats(errors, type, values.Lines.Select(l => (Guid?)l.ArticleId).ToList());
        return errors.Any ? errors.ToError() : null;
    }

    /// <summary>
    /// A replace that addresses no document (spec 005, R8: validation precedes the existence check): a quantity
    /// of 0 is acceptable on a count only, and there is no count here - so it is <c>VALIDATION_FAILED</c> with
    /// key <c>lines[i].quantity</c>, as it was before counts existed. Null when no line has a zero; the caller
    /// then answers "not found".
    /// </summary>
    public static AppError? WithoutDocument(StockDocumentValues values)
    {
        var errors = new ValidationErrors();
        for (var i = 0; i < values.Lines.Count; i++)
        {
            if (!QuantityRules.IsValid(values.Lines[i].Quantity))
                errors.Add(LineKey(i, "quantity"), QuantityMessage(type: null));
        }
        return errors.Any ? errors.ToError() : null;
    }

    // Spec 008, R3: each article at most once on a count, whatever the unit; every line of a repeated article is named.
    private static void Repeats(ValidationErrors errors, StockDocumentType type, IReadOnlyList<Guid?> articleIds)
    {
        if (type != StockDocumentType.Count)
            return;
        foreach (var i in CountRules.RepeatedLines(articleIds))
            errors.Add(LineKey(i, "articleId"),
                "A count names each article at most once: this article is on more than one line. Add the quantities up, in one unit, on a single line.");
    }

    /// <summary>
    /// Spec 009, R20: on a linked document every line names an order line, a JSON integer of 1 or more; on an
    /// unlinked document none does. <paramref name="linked"/> null: the request does not say (a replace) - then
    /// only the value itself is judged.
    /// </summary>
    private static string? OrderLineMessage(bool? linked, int? orderLineNo) => (linked, orderLineNo) switch
    {
        (true, null) => "orderLineNo is required on a document linked to an order: the number (1…n) of the order line this line fulfils.",
        (false, not null) => "orderLineNo is only for a document linked to an order; leave it out or pass null.",
        (_, < 1) => "orderLineNo must be an integer of 1 or more: the number of the order line this line fulfils.",
        _ => null,
    };

    private static string QuantityMessage(StockDocumentType? type) =>
        (type == StockDocumentType.Count
            ? "quantity is what was counted: 0 or greater (0 means none found)"
            : type is null
                ? "quantity must be 0 or greater (0 only on a count)"
                : "quantity must be greater than 0")
        + $", at most {QuantityRules.Max.ToString(CultureInfo.InvariantCulture)}, with at most {QuantityRules.DecimalPlaces} decimal places.";

    /// <summary>
    /// The destination rule (spec 006, R2, R3) for a document of a known type: null when
    /// <paramref name="toWarehouseId"/> is acceptable, otherwise <c>VALIDATION_FAILED</c> with key
    /// <c>toWarehouseId</c> - never <c>warehouseId</c>, also when the two are equal.
    /// </summary>
    public static AppError? Destination(StockDocumentType type, Guid warehouseId, Guid? toWarehouseId) =>
        DestinationMessage(type, warehouseId, toWarehouseId) is { } message ? AppError.Validation(ToWarehouseField, message) : null;

    private static string? DestinationMessage(StockDocumentType type, Guid warehouseId, Guid? toWarehouseId)
    {
        if (TransferRules.IsValidDestination(type, warehouseId, toWarehouseId))
            return null;
        if (type != StockDocumentType.Transfer)
            return $"toWarehouseId is only for a transfer; for {(type == StockDocumentType.Issue ? "an" : "a")} {type.ToName()} leave it out or pass null.";
        return toWarehouseId is null
            ? "toWarehouseId is required for a transfer: the id of the warehouse the goods go to."
            : "toWarehouseId must differ from warehouseId: a transfer moves goods to another warehouse.";
    }

    /// <summary>The body of a reversal (spec 006, R12): a real calendar date and an optional note.</summary>
    public static Result<StockReversalValues> Reverse(ReverseStockDocumentInput input)
    {
        var errors = new ValidationErrors();
        var date = Date(errors, input.DocumentDate);
        var note = Note(errors, input.Note);
        if (errors.Any)
            return errors.ToError();
        return new StockReversalValues(date, note);
    }

    /// <summary>Spec 006, R12: a reversal is not dated before the document it reverses.</summary>
    public static AppError? ReversalDate(DateOnly reversalDate, DateOnly originalDate) =>
        reversalDate >= originalDate
            ? null
            : AppError.Validation("documentDate",
                $"documentDate of a reversal must not be earlier than the date of the document it reverses, {originalDate.ToString(DateFormat, CultureInfo.InvariantCulture)}.");

    // R2: exactly YYYY-MM-DD and a real calendar date; any date, past or future.
    private static DateOnly Date(ValidationErrors errors, string? documentDate)
    {
        if (!DateOnly.TryParseExact(documentDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            errors.Add("documentDate", string.IsNullOrEmpty(documentDate)
                ? "documentDate is required, as YYYY-MM-DD."
                : "documentDate must be a calendar date written as YYYY-MM-DD, for example 2026-10-09.");
        return date;
    }

    private static string? Note(ValidationErrors errors, string? note)
    {
        if (!NoteRules.TryNormalize(note, StockDocument.NoteMaxLength, out var normalized))
            errors.Add("note", $"note must be at most {StockDocument.NoteMaxLength} characters and contain no control characters other than line breaks and tabs.");
        return normalized;
    }

    /// <param name="type">The type of the document; null when the request does not say (a replace): then a quantity of 0 passes here and <see cref="OfType"/> decides.</param>
    /// <param name="linked">Whether the request links the document to an order; null when it does not say (a replace).</param>
    private static StockDocumentValues Values(
        ValidationErrors errors, StockDocumentType? type, bool? linked, string? documentDate, string? warehouseId, string? toWarehouseId, string? reference, string? note,
        IReadOnlyList<StockLineInput?>? lines)
    {
        var date = Date(errors, documentDate);
        var warehouse = RequiredId(errors, warehouseId, "warehouseId", "a warehouse");
        // Only the form here: whether a destination is needed depends on the type (spec 006, R2, R3).
        Guid? toWarehouse = null;
        if (toWarehouseId is not null)
        {
            if (Guid.TryParse(toWarehouseId, out var parsed)) toWarehouse = parsed;
            else errors.Add(ToWarehouseField, "toWarehouseId must be the id (UUID) of a warehouse, not its code.");
        }

        if (!OptionalTextRules.TryNormalize(reference, StockDocument.ReferenceMaxLength, out var normalizedReference))
            errors.Add("reference", $"reference must be at most {StockDocument.ReferenceMaxLength} characters on a single line, without control characters.");
        var normalizedNote = Note(errors, note);

        var lineValues = new List<StockLineRequest>();
        var articleIds = new List<Guid?>();
        if (lines is null || lines.Count == 0)
            errors.Add("lines", $"lines is required: an array of 1 to {StockDocument.MaxLines} lines.");
        else if (lines.Count > StockDocument.MaxLines)
            errors.Add("lines", $"A document has at most {StockDocument.MaxLines} lines; split it into several documents.");
        else
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i] is not { } line)
                {
                    errors.Add($"lines[{i}]", "A line must be an object with articleId and quantity.");
                    articleIds.Add(null);
                    continue;
                }
                var article = RequiredId(errors, line.ArticleId, LineKey(i, "articleId"), "an article", "articleId");
                articleIds.Add(errors.Has(LineKey(i, "articleId")) ? null : article);
                // Spec 007, R12: optional; omitted or null is the article's base unit.
                Guid? unit = null;
                if (line.UnitId is not null)
                {
                    if (Guid.TryParse(line.UnitId, out var parsed)) unit = parsed;
                    else errors.Add(LineKey(i, UnitField),
                        "unitId must be the id (UUID) of a unit of measure, not its code; leave it out or pass null for the article's base unit.");
                }
                if (OrderLineMessage(linked, line.OrderLineNo) is { } orderLineMessage)
                    errors.Add(LineKey(i, OrderLineField), orderLineMessage);
                if (line.Quantity is not { } quantity)
                    errors.Add(LineKey(i, "quantity"), "quantity is required.");
                else if (!QuantityRules.IsValidOn(type ?? StockDocumentType.Count, quantity))
                    errors.Add(LineKey(i, "quantity"), QuantityMessage(type));
                else
                    lineValues.Add(new StockLineRequest(article, quantity, unit, line.OrderLineNo));
            }
            if (type is { } known)
                Repeats(errors, known, articleIds);
        }
        return new StockDocumentValues(date, warehouse, toWarehouse, normalizedReference, normalizedNote, lineValues);
    }

    public static Result<StockDocumentListQuery> List(ListStockDocumentsInput input)
    {
        var errors = new ValidationErrors();
        StockDocumentType? type = null;
        if (!string.IsNullOrEmpty(input.Type))
        {
            if (StockDocumentTypeNames.TryParse(input.Type, out var parsed)) type = parsed;
            else errors.Add("type", TypeMessage);
        }
        StockDocumentStatus? status = null;
        if (!string.IsNullOrEmpty(input.Status))
        {
            if (StockDocumentStatusNames.TryParse(input.Status, out var parsed)) status = parsed;
            else errors.Add("status", StatusMessage);
        }
        var warehouseId = OptionalId(errors, input.WarehouseId, "warehouseId");
        var purchaseOrderId = OptionalId(errors, input.PurchaseOrderId, PurchaseOrderField);
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new StockDocumentListQuery(type, status, warehouseId, search, limit, offset, purchaseOrderId);
    }

    public static Result<StockOnHandQuery> OnHand(ListStockOnHandInput input)
    {
        var errors = new ValidationErrors();
        var articleId = OptionalId(errors, input.ArticleId, "articleId");
        var warehouseId = OptionalId(errors, input.WarehouseId, "warehouseId");
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new StockOnHandQuery(articleId, warehouseId, limit, offset);
    }

    public static Result<StockLedgerQuery> Ledger(ListStockLedgerEntriesInput input)
    {
        var errors = new ValidationErrors();
        var articleId = OptionalId(errors, input.ArticleId, "articleId");
        var warehouseId = OptionalId(errors, input.WarehouseId, "warehouseId");
        var documentId = OptionalId(errors, input.DocumentId, "documentId");
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new StockLedgerQuery(articleId, warehouseId, documentId, limit, offset);
    }

    /// <summary>Only the form is checked here; whether the record exists is a later stage (R8).</summary>
    private static Guid RequiredId(ValidationErrors errors, string? input, string key, string what, string? field = null)
    {
        if (Guid.TryParse(input, out var id))
            return id;
        field ??= key;
        errors.Add(key, string.IsNullOrEmpty(input) ? $"{field} is required." : $"{field} must be the id (UUID) of {what}, not its code.");
        return default;
    }

    private static Guid? OptionalId(ValidationErrors errors, string? input, string field)
    {
        if (string.IsNullOrEmpty(input))
            return null;
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add(field, $"{field} must be a UUID.");
        return null;
    }
}
