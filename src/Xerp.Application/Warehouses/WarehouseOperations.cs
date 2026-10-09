using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Warehouses;

/// <summary>
/// The warehouse operations of spec 004, section 4. One method = one HTTP endpoint = one MCP tool.
/// <see cref="IXerpDb"/> is already filtered to the current tenant, so a warehouse of another tenant is
/// simply not found here. Writes check in the order of R11: validation -> addressed warehouse -> code uniqueness.
/// </summary>
public sealed class WarehouseOperations(IXerpDb db, ITenantContext context, IClock clock)
{
    public async Task<Result<PagedResult<WarehouseDto>>> ListAsync(ListWarehousesInput input, CancellationToken cancellationToken = default)
    {
        var validated = WarehouseValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var warehouses = db.Warehouses.AsNoTracking();
        if (query.IsActive is { } isActive)
            warehouses = warehouses.Where(w => w.IsActive == isActive);
        if (query.Search is { } search)
        {
            // Code and name - not the address (R17).
            var pattern = LikePattern.Contains(search);
            warehouses = warehouses.Where(w =>
                EF.Functions.Like(EF.Property<string>(w, DbNames.CodeLower), DbText.Lower(pattern), LikePattern.EscapeCharacter) ||
                EF.Functions.Like(DbText.Lower(w.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }

        var total = await warehouses.CountAsync(cancellationToken);
        var page = await warehouses
            .OrderBy(w => EF.Property<string>(w, DbNames.CodeLower))
            .ThenBy(w => w.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        return new PagedResult<WarehouseDto>(page.Select(ToDto).ToList(), total, query.Limit, query.Offset);
    }

    public async Task<Result<WarehouseDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var warehouse = await db.Warehouses.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        return warehouse is null ? NotFound() : ToDto(warehouse);
    }

    public async Task<Result<WarehouseDto>> GetByCodeAsync(string? code, CancellationToken cancellationToken = default)
    {
        // A string that cannot be a code cannot name a warehouse.
        if (!CodeRules.TryNormalize(code, out var normalized))
            return NotFound();
        var warehouse = await db.Warehouses.AsNoTracking()
            .SingleOrDefaultAsync(w => EF.Property<string>(w, DbNames.CodeLower) == DbText.Lower(normalized), cancellationToken);
        return warehouse is null ? NotFound() : ToDto(warehouse);
    }

    public async Task<Result<WarehouseDto>> CreateAsync(WarehouseInput input, CancellationToken cancellationToken = default)
    {
        var validated = WarehouseValidation.Create(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        if (await CodeIsUsedAsync(values.Code, exceptId: null, cancellationToken))
            return CodeTaken(values.Code);

        var warehouse = Warehouse.Create(values.Code, values.Name, values.Address, values.IsActive, clock.UtcNow, ActorKeyId());
        db.Warehouses.Add(warehouse);
        return await SaveAsync(warehouse, values.Code, cancellationToken);
    }

    public async Task<Result<WarehouseDto>> ReplaceAsync(Guid id, WarehouseInput input, CancellationToken cancellationToken = default)
    {
        var validated = WarehouseValidation.Replace(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (warehouse is null)
            return NotFound();
        if (await CodeIsUsedAsync(values.Code, exceptId: id, cancellationToken))
            return CodeTaken(values.Code);

        warehouse.Replace(values.Code, values.Name, values.Address, values.IsActive, clock.UtcNow, ActorKeyId());
        return await SaveAsync(warehouse, values.Code, cancellationToken);
    }

    public async Task<Result<WarehouseDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var warehouse = await db.Warehouses.SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (warehouse is null)
            return NotFound();
        // A draft counts like a posted document (spec 005, R24, R25).
        if (await db.StockDocuments.AnyAsync(d => d.WarehouseId == id || d.ToWarehouseId == id, cancellationToken))
            return InUse();
        // Spec 009, R37: so does an order of any status.
        if (await db.PurchaseOrders.AnyAsync(o => o.WarehouseId == id, cancellationToken))
            return InUse();
        db.Warehouses.Remove(warehouse);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ForeignKeyViolationException ex) when (ex.BlockedDelete)
        {
            // A document started to use the warehouse after the check above: the foreign key is the authority.
            return InUse();
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
        return new WarehouseDeleted();
    }

    /// <summary>Saves the tracked warehouse, translating what only the database can decide.</summary>
    private async Task<Result<WarehouseDto>> SaveAsync(Warehouse warehouse, string code, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException ex) when (ex.IsCodeOf<Warehouse>())
        {
            // Lost a race with a concurrent write of the same code: the unique index is the authority.
            return CodeTaken(code);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by someone else between the read and the write.
            return NotFound();
        }
        return ToDto(warehouse);
    }

    private Task<bool> CodeIsUsedAsync(string code, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Warehouses.AnyAsync(
            w => EF.Property<string>(w, DbNames.CodeLower) == DbText.Lower(code) && (exceptId == null || w.Id != exceptId),
            cancellationToken);

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A warehouse can only be written by a tenant API key.");

    /// <summary>By <c>id</c> or by <c>code</c>, exactly one (spec 003, R13) - for clients without a URL path.</summary>
    public Task<Result<WarehouseDto>> FindAsync(RecordAddressInput address, CancellationToken cancellationToken = default)
    {
        if (RecordAddress.IdOrCode(address, NotFoundDetail, out var id, out var code) is { } error)
            return Task.FromResult<Result<WarehouseDto>>(error);
        return id is { } byId ? GetAsync(byId, cancellationToken) : GetByCodeAsync(code, cancellationToken);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<WarehouseDto>> ReplaceAsync(string? id, WarehouseInput input, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<WarehouseDto>>(error)
            : ReplaceAsync(parsed, input, cancellationToken);

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no record (spec 003, R15).</summary>
    public Task<Result<WarehouseDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<WarehouseDeleted>>(error)
            : DeleteAsync(parsed, cancellationToken);

    private static AppError InUse() =>
        AppError.InUse("The warehouse is used by stock documents or orders and cannot be deleted. Deactivate it instead (isActive = false).");

    private const string NotFoundDetail = "Warehouse not found.";

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private static AppError CodeTaken(string code) => AppError.CodeTaken($"A warehouse with code '{code}' already exists.");

    private static WarehouseDto ToDto(Warehouse w) =>
        new(w.Id, w.Code, w.Name,
            w.Address.Line1, w.Address.Line2, w.Address.PostalCode, w.Address.City, w.Address.Region, w.Address.CountryCode,
            w.IsActive, w.CreatedAt, w.UpdatedAt, w.CreatedBy, w.UpdatedBy);
}
