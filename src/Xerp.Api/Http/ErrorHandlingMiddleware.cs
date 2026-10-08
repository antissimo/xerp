using Xerp.Application.Common;

namespace Xerp.Api.Http;

/// <summary>Unexpected failures become <c>500 INTERNAL_ERROR</c> with no detail about the cause (spec 001, S6).</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            // The server refused to read the request (for example, the body is too large).
            logger.LogInformation(ex, "Bad request on {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.Clear();
            await Problems.WriteAsync(context, AppError.Validation("body", "The request could not be read."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;
            context.Response.Clear();
            await Problems.WriteAsync(context, AppError.Internal());
        }
    }
}
