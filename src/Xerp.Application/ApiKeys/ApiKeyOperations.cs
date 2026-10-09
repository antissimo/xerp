using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.ApiKeys;

/// <summary>
/// API key management (spec 003, R1-R10; ADR-0010): create, list, get, revoke. Keys are never edited
/// and never deleted. <see cref="IXerpDb.ApiKeys"/> is filtered to the current tenant.
/// </summary>
public sealed class ApiKeyOperations(
    IXerpDb db,
    ITenantContext context,
    IClock clock,
    IApiKeyGenerator keyGenerator,
    IApiKeyHasher keyHasher)
{
    private const string NotFoundDetail = "API key not found.";

    public async Task<Result<PagedResult<ApiKeyDto>>> ListAsync(ListApiKeysInput input, CancellationToken cancellationToken = default)
    {
        var validated = ApiKeyValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var query = validated.Value;

        var keys = db.ApiKeys.AsNoTracking();
        if (query.ActorType is { } actorType)
            keys = keys.Where(k => k.ActorType == actorType);
        if (query.IsActive is { } isActive)
            keys = keys.Where(k => k.IsActive == isActive);
        if (query.Search is { } search)
        {
            var pattern = LikePattern.Contains(search);
            keys = keys.Where(k => EF.Functions.Like(DbText.Lower(k.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }

        var total = await keys.CountAsync(cancellationToken);
        var items = await keys
            .OrderBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        return new PagedResult<ApiKeyDto>(items.Select(ToDto).ToList(), total, query.Limit, query.Offset);
    }

    public async Task<Result<ApiKeyDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken);
        return key is null ? NotFound() : ToDto(key);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no key (R15).</summary>
    public Task<Result<ApiKeyDto>> GetAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<ApiKeyDto>>(error)
            : GetAsync(parsed, cancellationToken);

    /// <summary>The returned DTO is the only place the secret ever appears; only its hash is stored (R3).</summary>
    public async Task<Result<CreatedApiKeyDto>> CreateAsync(CreateApiKeyInput input, CancellationToken cancellationToken = default)
    {
        var validated = ApiKeyValidation.Create(input);
        if (!validated.IsSuccess)
            return validated.Error;
        if (context.TenantId is not { } tenantId || context.ApiKeyId is not { } actingKeyId)
            return AppError.Unauthenticated();

        var secret = keyGenerator.Generate();
        var key = ApiKey.Create(
            tenantId, validated.Value.Name, validated.Value.ActorType, keyHasher.Hash(secret), clock.UtcNow, actingKeyId);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);

        return new CreatedApiKeyDto(
            key.Id, key.Name, key.ActorType.ToName(), key.IsActive, key.CreatedAt, key.CreatedBy, key.RevokedAt, key.RevokedBy, secret);
    }

    public async Task<Result<ApiKeyDto>> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (context.ApiKeyId is not { } actingKeyId)
            return AppError.Unauthenticated();
        // R7: checked first, so it also holds for a key that is already inactive.
        if (id == actingKeyId)
            return new AppError(ErrorCodes.CannotRevokeSelf,
                "A key cannot revoke itself. Use another key of the tenant to revoke this one.");

        // R8: revocations of one tenant run one after another, and each re-reads the acting key after it
        // got its turn. Of two keys revoking each other, the second therefore finds itself revoked and is
        // rejected, so a tenant can never lose its last active key.
        return await db.SerializedPerTenantAsync<Result<ApiKeyDto>>(async ct =>
        {
            var actingKeyIsActive = await db.ApiKeys.AsNoTracking()
                .Where(k => k.Id == actingKeyId)
                .Select(k => (bool?)k.IsActive)
                .SingleOrDefaultAsync(ct);
            if (actingKeyIsActive != true)
                return AppError.Unauthenticated();

            var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == id, ct);
            if (key is null)
                return NotFound();
            key.Revoke(clock.UtcNow, actingKeyId);
            await db.SaveChangesAsync(ct);
            return ToDto(key);
        }, cancellationToken);
    }

    /// <summary>As the overload with a <see cref="Guid"/>; an id that is not a UUID names no key (R15).</summary>
    public Task<Result<ApiKeyDto>> RevokeAsync(string? id, CancellationToken cancellationToken = default) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error
            ? Task.FromResult<Result<ApiKeyDto>>(error)
            : RevokeAsync(parsed, cancellationToken);

    private static ApiKeyDto ToDto(ApiKey key) =>
        new(key.Id, key.Name, key.ActorType.ToName(), key.IsActive, key.CreatedAt, key.CreatedBy, key.RevokedAt, key.RevokedBy);

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);
}
