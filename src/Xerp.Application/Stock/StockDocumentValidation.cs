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
        $"type must be \"{StockDocumentTypeNames.Receipt}\" or \"{StockDocumentTypeNames.Issue}\".";

    private const string StatusMessage =
        $"status must be \"{StockDocumentStatusNames.Draft}\" or \"{StockDocumentStatusNames.Posted}\".";

    public static string LineKey(int index, string field) => $"lines[{index}].{field}";

    public static Result<NewStockDocumentValues> Create(CreateStockDocumentInput input)
    {
        var errors = new ValidationErrors();
        if (!StockDocumentTypeNames.TryParse(input.Type, out var type))
            errors.Add("type", string.IsNullOrEmpty(input.Type) ? "type is required. " + TypeMessage : TypeMessage);
        var values = Values(errors, input.DocumentDate, input.WarehouseId, input.Reference, input.Note, input.Lines);
        if (errors.Any)
            return errors.ToError();
        return new NewStockDocumentValues(type, values);
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
        var values = Values(errors, input.DocumentDate, input.WarehouseId, input.Reference, input.Note, input.Lines);
        if (errors.Any)
            return errors.ToError();
        return values;
    }

    private static StockDocumentValues Values(
        ValidationErrors errors, string? documentDate, string? warehouseId, string? reference, string? note,
        IReadOnlyList<StockLineInput?>? lines)
    {
        // R2: exactly YYYY-MM-DD and a real calendar date; any date, past or future.
        if (!DateOnly.TryParseExact(documentDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            errors.Add("documentDate", string.IsNullOrEmpty(documentDate)
                ? "documentDate is required, as YYYY-MM-DD."
                : "documentDate must be a calendar date written as YYYY-MM-DD, for example 2026-10-09.");

        var warehouse = RequiredId(errors, warehouseId, "warehouseId", "a warehouse");

        if (!OptionalTextRules.TryNormalize(reference, StockDocument.ReferenceMaxLength, out var normalizedReference))
            errors.Add("reference", $"reference must be at most {StockDocument.ReferenceMaxLength} characters on a single line, without control characters.");
        if (!NoteRules.TryNormalize(note, StockDocument.NoteMaxLength, out var normalizedNote))
            errors.Add("note", $"note must be at most {StockDocument.NoteMaxLength} characters and contain no control characters other than line breaks and tabs.");

        var lineValues = new List<StockLineValues>();
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
                    continue;
                }
                var article = RequiredId(errors, line.ArticleId, LineKey(i, "articleId"), "an article", "articleId");
                if (line.Quantity is not { } quantity)
                    errors.Add(LineKey(i, "quantity"), "quantity is required.");
                else if (!QuantityRules.IsValid(quantity))
                    errors.Add(LineKey(i, "quantity"),
                        $"quantity must be greater than 0, at most {QuantityRules.Max.ToString(CultureInfo.InvariantCulture)}, with at most {QuantityRules.DecimalPlaces} decimal places.");
                else
                    lineValues.Add(new StockLineValues(article, quantity));
            }
        }
        return new StockDocumentValues(date, warehouse, normalizedReference, normalizedNote, lineValues);
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
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new StockDocumentListQuery(type, status, warehouseId, search, limit, offset);
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
