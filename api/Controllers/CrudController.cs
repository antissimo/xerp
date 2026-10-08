using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xerp.Api.Data;
using Xerp.Api.Entities;

namespace Xerp.Api.Controllers;

public record ListResult<T>(IReadOnlyList<T> Items, int Total, int Limit, int Offset);

/// <summary>
/// The CRUD endpoints shared by every basic entity. A concrete controller only
/// sets the route and maps its own extra fields in <see cref="Apply"/>.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class CrudController<TEntity, TInput>(AppDb db) : ControllerBase
    where TEntity : Entity, new()
    where TInput : EntityInput
{
    private const int MaxLimit = 500;

    /// <summary>Copies client input onto the entity. Override to map extra fields.</summary>
    protected virtual void Apply(TInput input, TEntity entity)
    {
        entity.Code = input.Code.Trim();
        entity.Name = input.Name.Trim();
        entity.IsActive = input.IsActive;
    }

    [HttpGet]
    public async Task<ListResult<TEntity>> List(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        offset = Math.Max(offset, 0);

        var query = db.Set<TEntity>().AsNoTracking();
        if (isActive is not null)
            query = query.Where(e => e.IsActive == isActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.Code, pattern) || EF.Functions.ILike(e.Name, pattern));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(e => e.Code).Skip(offset).Take(limit).ToListAsync(ct);
        return new ListResult<TEntity>(items, total, limit, offset);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TEntity>> Get(Guid id, CancellationToken ct)
    {
        var entity = await db.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
            return NotFoundError(id.ToString());
        return entity;
    }

    [HttpGet("by-code/{code}")]
    public async Task<ActionResult<TEntity>> GetByCode(string code, CancellationToken ct)
    {
        var entity = await db.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Code == code, ct);
        if (entity is null)
            return NotFoundError(code);
        return entity;
    }

    [HttpPost]
    public async Task<ActionResult<TEntity>> Create(TInput input, CancellationToken ct)
    {
        var entity = new TEntity();
        Apply(input, entity);
        db.Add(entity);

        if (await SaveAsync(ct) is { } error)
            return error;
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, entity);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TEntity>> Update(Guid id, TInput input, CancellationToken ct)
    {
        var entity = await db.Set<TEntity>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
            return NotFoundError(id.ToString());
        Apply(input, entity);

        if (await SaveAsync(ct) is { } error)
            return error;
        return entity;
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.Set<TEntity>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
            return NotFoundError(id.ToString());
        db.Remove(entity);

        if (await SaveAsync(ct) is { } error)
            return error;
        return NoContent();
    }

    /// <summary>Saves, turning constraint violations into 409 responses. Returns null on success.</summary>
    private async Task<ObjectResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error(StatusCodes.Status409Conflict, "CODE_TAKEN",
                $"Another {typeof(TEntity).Name} already uses this code.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return Error(StatusCodes.Status409Conflict, "IN_USE",
                $"This {typeof(TEntity).Name} is referenced by other records. Set isActive to false instead.");
        }
    }

    private ObjectResult NotFoundError(string key) =>
        Error(StatusCodes.Status404NotFound, "NOT_FOUND", $"No {typeof(TEntity).Name} found for '{key}'.");

    private ObjectResult Error(int status, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
