using Microsoft.AspNetCore.Mvc;

namespace Glazion.Api.Contracts;

/// <summary>A request the API refuses with a typed problem (RFC 7807) rather than a partial or fabricated result.</summary>
public sealed class ApiProblemException(int status, string code, string title, string detail, IDictionary<string, object?>? extensions = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public IDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}

/// <summary>Phase-two (/api/v2) error shape: RFC 7807 problem details with a stable machine-readable "code" (PRD §10, §13).</summary>
public static class ApiProblems
{
    public static ApiProblemException Validation(string field, string message) =>
        new(400, "validation_error", "Validation failed", message, new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { [field] = [message] } });

    public static ApiProblemException NotFound(string what) => new(404, "not_found", "Not found", $"{what} was not found.");

    public static ApiProblemException Conflict(string code, string detail) => new(409, code, "Conflict", detail);

    public static ObjectResult ToResult(HttpContext http, ApiProblemException ex) =>
        new(Build(http, ex)) { StatusCode = ex.Status, ContentTypes = { "application/problem+json" } };

    public static ProblemDetails Build(HttpContext http, ApiProblemException ex)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://api.glazion.app/problems/{ex.Code.Replace('_', '-')}",
            Title = ex.Title,
            Status = ex.Status,
            Detail = ex.Message,
            Instance = http.Request.Path
        };
        problem.Extensions["code"] = ex.Code;
        problem.Extensions["traceId"] = http.TraceIdentifier;
        foreach (var (key, value) in ex.Extensions) problem.Extensions[key] = value;
        return problem;
    }
}
