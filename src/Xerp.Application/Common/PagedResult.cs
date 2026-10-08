namespace Xerp.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Limit, int Offset);
