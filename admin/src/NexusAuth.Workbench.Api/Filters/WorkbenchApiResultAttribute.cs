using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using NexusAuth.Application.Clients;
using Npgsql;

namespace NexusAuth.Workbench.Api.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class WorkbenchApiResultAttribute : TypeFilterAttribute
{
    public WorkbenchApiResultAttribute()
        : base(typeof(WorkbenchApiResultFilter))
    {
    }
}

public sealed class WorkbenchApiResultFilter(ILogger<WorkbenchApiResultFilter> logger)
    : IAsyncAlwaysRunResultFilter, IAsyncExceptionFilter
{
    private const string UnexpectedErrorMessage = "An unexpected error occurred. Please try again later.";
    private const string DatabaseConflictMessage = "The requested change conflicts with an existing record.";

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        context.Result = WrapResult(context.Result);
        return next();
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        var (statusCode, message) = context.Exception switch
        {
            KeyNotFoundException exception => (StatusCodes.Status404NotFound, UseMessageOrFallback(exception.Message, StatusCodes.Status404NotFound)),
            ArgumentException exception => (StatusCodes.Status400BadRequest, UseMessageOrFallback(exception.Message, StatusCodes.Status400BadRequest)),
            WorkbenchBusinessConflictException exception => (StatusCodes.Status409Conflict, UseMessageOrFallback(exception.Message, StatusCodes.Status409Conflict)),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, DatabaseConflictMessage),
            DbUpdateException exception when IsUniqueConstraintViolation(exception) => (StatusCodes.Status409Conflict, DatabaseConflictMessage),
            _ => (StatusCodes.Status500InternalServerError, UnexpectedErrorMessage),
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                context.Exception,
                "Workbench API request failed. Method={Method} Path={Path} TraceId={TraceId}",
                context.HttpContext.Request.Method,
                context.HttpContext.Request.Path,
                context.HttpContext.TraceIdentifier);
        }

        context.Result = ErrorResult(statusCode, message);
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }

    private static IActionResult WrapResult(IActionResult result)
    {
        if (result is NoContentResult)
            return result;

        if (result is ChallengeResult)
            return ErrorResult(StatusCodes.Status401Unauthorized, DefaultErrorMessage(StatusCodes.Status401Unauthorized));

        if (result is ForbidResult)
            return ErrorResult(StatusCodes.Status403Forbidden, DefaultErrorMessage(StatusCodes.Status403Forbidden));

        if (result is ObjectResult objectResult)
        {
            var statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
            if (statusCode == StatusCodes.Status204NoContent)
                return new NoContentResult();

            return IsSuccess(statusCode)
                ? SuccessResult(statusCode, objectResult.Value, objectResult.ContentTypes)
                : ErrorResult(
                    statusCode,
                    ExtractMessage(objectResult.Value, statusCode),
                    objectResult.ContentTypes,
                    objectResult.Value as ValidationProblemDetails);
        }

        if (result is StatusCodeResult statusCodeResult)
        {
            if (statusCodeResult.StatusCode == StatusCodes.Status204NoContent)
                return result;

            return IsSuccess(statusCodeResult.StatusCode)
                ? SuccessResult(statusCodeResult.StatusCode, null)
                : ErrorResult(statusCodeResult.StatusCode, DefaultErrorMessage(statusCodeResult.StatusCode));
        }

        if (result is EmptyResult)
            return SuccessResult(StatusCodes.Status200OK, null);

        return result;
    }

    private static ObjectResult SuccessResult(
        int statusCode,
        object? value,
        IList<string>? contentTypes = null)
    {
        var result = new ObjectResult(new
        {
            success = true,
            result = value,
        })
        {
            StatusCode = statusCode,
        };
        CopyContentTypes(contentTypes, result);
        return result;
    }

    private static ObjectResult ErrorResult(
        int statusCode,
        string message,
        IList<string>? contentTypes = null,
        ValidationProblemDetails? validationProblem = null)
    {
        object value = validationProblem is null
            ? new
            {
                success = false,
                message,
                errorMessage = message,
            }
            : new
            {
                success = false,
                message,
                errorMessage = message,
                errors = validationProblem.Errors,
            };
        var result = new ObjectResult(value)
        {
            StatusCode = statusCode,
        };
        CopyContentTypes(contentTypes, result);
        return result;
    }

    private static void CopyContentTypes(IList<string>? contentTypes, ObjectResult result)
    {
        if (contentTypes is null)
            return;

        foreach (var contentType in contentTypes)
            result.ContentTypes.Add(contentType);
    }

    private static string ExtractMessage(object? value, int statusCode)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
            return text;

        if (value is ValidationProblemDetails validation)
        {
            var validationMessage = validation.Errors
                .SelectMany(entry => entry.Value)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
            if (!string.IsNullOrWhiteSpace(validationMessage))
                return validationMessage;
        }

        if (value is ProblemDetails problem)
        {
            if (!string.IsNullOrWhiteSpace(problem.Detail))
                return problem.Detail;
        }

        try
        {
            var element = value is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "message", "errorMessage", "detail" })
                {
                    if (element.TryGetProperty(propertyName, out var property)
                        && property.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.GetString()))
                    {
                        return property.GetString()!;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return DefaultErrorMessage(statusCode);
    }

    private static string UseMessageOrFallback(string message, int statusCode) =>
        string.IsNullOrWhiteSpace(message) ? DefaultErrorMessage(statusCode) : message;

    private static bool IsSuccess(int statusCode) =>
        statusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices;

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string DefaultErrorMessage(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "The request is invalid.",
        StatusCodes.Status401Unauthorized => "Authentication is required.",
        StatusCodes.Status403Forbidden => "You do not have permission to perform this action.",
        StatusCodes.Status404NotFound => "The requested resource was not found.",
        StatusCodes.Status409Conflict => "The request conflicts with the current state.",
        _ => "The request could not be completed.",
    };
}
