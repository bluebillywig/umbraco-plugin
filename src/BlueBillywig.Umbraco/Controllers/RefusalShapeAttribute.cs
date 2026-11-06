using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BlueBillywig.Umbraco.Controllers;

/// <summary>
/// Turns every refusal (an <see cref="ObjectResult"/> with status 400 or above) into RFC 7807 problem details that carry the
/// controller's sentence as <c>detail</c> and as an <c>error</c> extension. The backoffice's API error interceptor keeps a
/// non-2xx body only when <c>type</c>, <c>title</c> and <c>status</c> are all present and otherwise replaces it with a
/// synthetic problem whose title is the HTTP status text, so a bare <c>{ error = "..." }</c> reached the editor as just
/// "Bad Request". Also covers the automatic model-validation 400 (the first validation message becomes the sentence).
/// Results that are not object results (401/403 challenges, status-only results) are left alone.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RefusalShapeAttribute : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult result && Shape(result) is { } shaped)
        {
            context.Result = shaped;
        }
    }

    internal static ObjectResult? Shape(ObjectResult result)
    {
        int status = result.StatusCode ?? (result.Value as ProblemDetails)?.Status ?? StatusCodes.Status200OK;
        if (status < StatusCodes.Status400BadRequest)
        {
            return null;
        }

        string? sentence;
        ProblemDetails problem;
        switch (result.Value)
        {
            case ValidationProblemDetails validation:
                sentence = FirstNonEmpty(validation.Detail) ?? validation.Errors.SelectMany(e => e.Value).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                problem = validation;
                break;
            case ProblemDetails existing:
                sentence = FirstNonEmpty(existing.Detail);
                problem = existing;
                break;
            default:
                sentence = SentenceOf(result.Value);
                problem = new ProblemDetails();
                break;
        }

        problem.Type ??= "BlueBillywig.Refused";
        problem.Status = status;
        problem.Title ??= TitleOf(status);
        if (sentence is not null)
        {
            problem.Detail ??= sentence;
            problem.Extensions["error"] = sentence;
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    private static string? FirstNonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The <c>error</c> string of the anonymous body the controllers return (<c>new { error = "..." }</c>), if any.</summary>
    private static string? SentenceOf(object? value)
    {
        if (value is null or string)
        {
            return value as string is { Length: > 0 } s ? s : null;
        }

        try
        {
            JsonElement element = JsonSerializer.SerializeToElement(value);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in element.EnumerateObject())
                {
                    if (p.Name.Equals("error", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    {
                        return FirstNonEmpty(p.Value.GetString());
                    }
                }
            }
        }
        catch (NotSupportedException)
        {
        }

        return null;
    }

    private static string TitleOf(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Entity",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "Request refused",
    };
}
