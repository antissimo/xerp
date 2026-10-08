using Xerp.Application.Common;

namespace Xerp.Api.Http;

/// <summary>The query parameters an endpoint defines; an endpoint without this metadata defines none.</summary>
public sealed record AcceptedQuery(IReadOnlySet<string> Names);

/// <summary>
/// Spec 001, R15: a query parameter the operation does not define, or one given more than once, is a
/// validation error under its own name. Names are case-sensitive.
/// </summary>
public static class QueryParameters
{
    public static TBuilder AcceptsQuery<TBuilder>(this TBuilder builder, params string[] names)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AcceptedQuery(new HashSet<string>(names, StringComparer.Ordinal)));

    public static RouteGroupBuilder RejectUndefinedQueryParameters(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var accepted = http.GetEndpoint()?.Metadata.GetMetadata<AcceptedQuery>()?.Names;
            var errors = new ValidationErrors();
            foreach (var (name, values) in http.Request.Query)
            {
                if (accepted is null || !accepted.Contains(name))
                    errors.Add(name, "This operation has no such query parameter.");
                else if (values.Count > 1)
                    errors.Add(name, $"{name} may be given only once.");
            }
            return errors.Any ? Problems.From(errors.ToError()) : await next(context);
        });
        return group;
    }
}
