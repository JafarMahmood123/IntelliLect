using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UserManagementService.Application.Common;

namespace UserManagementService.Api.Middleware;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    /// <summary>
    /// Nginx's "Client Closed Request". Not an IANA code and not in <c>StatusCodes</c>, but it is
    /// what the proxy in front of this service already writes for the same situation, so logs and
    /// dashboards on both sides agree. Nobody receives it — the connection is gone by definition.
    /// </summary>
    private const int ClientClosedRequest = 499;

    private const string UnexpectedFailureDetail =
        "The request could not be completed. If it keeps happening, quote the time it occurred.";

    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A client that hung up is not a server error. ASP.NET cancels the request token when the
        // connection drops, EF surfaces that as OperationCanceledException, and treating it as
        // unhandled fills the log with 500s for requests nobody is waiting on — in bursts,
        // because a timeout is usually followed by a retry. That noise arrives at precisely the
        // moment somebody is reading the log to find out what went wrong.
        if (exception is OperationCanceledException)
        {
            _logger.LogInformation(
                "Request to {Path} was abandoned by the caller before it completed.",
                httpContext.Request.Path);

            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        _logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

        // Past the first byte there is no status line left to change and no body to replace.
        // Appending a ProblemDetails here would glue an error object onto a half-written payload
        // under whatever status was already promised. Returning false lets the middleware abort
        // the connection, which at least fails visibly.
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        // Map specific exceptions to HTTP Status Codes
        var (statusCode, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Data Conflict"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Conflict"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error")
        };

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            // The mapped exceptions are ours and their messages are written to be read by a user.
            // The catch-all is by definition the case where nobody decided that, and those
            // messages carry whatever the failing component had to hand: Npgsql includes the SQL,
            // the table and the constraint, and a configuration failure includes the connection
            // string it tried. The detail stays in the log, which is not a public surface.
            Detail = statusCode == StatusCodes.Status500InternalServerError
                ? UnexpectedFailureDetail
                : exception.Message,
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = statusCode;

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true; // Indicates we handled the exception
    }
}
