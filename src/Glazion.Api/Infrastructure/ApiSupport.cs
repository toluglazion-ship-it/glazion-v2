using System.Security.Claims;
using System.Text;
using Glazion.Api.Contracts;
using Glazion.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException());
    public static Guid? GetSessionId(this ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue(GlazionClaims.SessionId), out var id) ? id : null;
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(UserRole.Admin.ToString());
}

/// <summary>
/// Opaque cursor paging for PagedResult (items, totalCount, nextCursor). The cursor is the next offset, base64url
/// encoded; clients treat it as opaque and follow nextCursor until it is null.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>True when the caller asked for a page (cursor or pageSize), for lists that page only on request.</summary>
    public static bool Requested(string? cursor, int? pageSize) => !string.IsNullOrWhiteSpace(cursor) || pageSize is not null;

    public static async Task<PagedResult<TOut>> PageAsync<T, TOut>(IQueryable<T> ordered, string? cursor, int? pageSize, Func<List<T>, Task<IReadOnlyList<TOut>>> map, CancellationToken cancellationToken, int maxPageSize = MaxPageSize)
    {
        var size = Math.Clamp(pageSize ?? Math.Min(DefaultPageSize, maxPageSize), 1, maxPageSize);
        var offset = Decode(cursor);
        var total = await ordered.CountAsync(cancellationToken);
        var rows = await ordered.Skip(offset).Take(size).ToListAsync(cancellationToken);
        var next = offset + rows.Count < total ? Encode(offset + rows.Count) : null;
        return new PagedResult<TOut>(await map(rows), total, next);
    }

    /// <summary>The same page shape over a list already in memory (materials, duplicates).</summary>
    public static PagedResult<T> Page<T>(IReadOnlyList<T> all, string? cursor, int? pageSize, int maxPageSize = MaxPageSize)
    {
        var size = Math.Clamp(pageSize ?? Math.Min(DefaultPageSize, maxPageSize), 1, maxPageSize);
        var offset = Decode(cursor);
        var rows = all.Skip(offset).Take(size).ToList();
        return new PagedResult<T>(rows, all.Count, offset + rows.Count < all.Count ? Encode(offset + rows.Count) : null);
    }

    public static string Encode(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"o:{offset}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static int Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            if (text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], out var offset) && offset >= 0) return offset;
        }
        catch (FormatException) { }
        throw ApiProblems.Validation("cursor", "The cursor is not valid. Start again without one.");
    }
}

/// <summary>Capture codes such as GLZ-LAB-A-8Q4K-2M7P (plan §7.2). Unambiguous alphabet, cryptographic randomness.</summary>
public static class CaptureCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Create(string? labelPrefix)
    {
        static string Block() => string.Create(4, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)]; });
        return string.IsNullOrWhiteSpace(labelPrefix) ? $"GLZ-{Block()}-{Block()}" : $"GLZ-{labelPrefix.Trim().ToUpperInvariant()}-{Block()}-{Block()}";
    }

    public static string Hash(string code) => TokenService.HashToken(code.Trim().ToUpperInvariant());
    public static string Suffix(string code) => code[^4..];
    public static bool IsValidLabelPrefix(string value) => value.Length is >= 1 and <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

/// <summary>
/// Typed refusals thrown by controllers become problem details here, before the exception middleware would log them
/// as server errors. They are expected outcomes (wrong password, conflict), not faults.
/// </summary>
public sealed class ApiProblemExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (ApiExceptionHandler.ToProblem(context.Exception) is not { } problem) return;
        context.Result = ApiProblems.ToResult(context.HttpContext, problem);
        context.ExceptionHandled = true;
    }
}

/// <summary>Maps typed refusals thrown outside MVC to RFC 7807 problem details with a stable "code".</summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public static ApiProblemException? ToProblem(Exception exception) => exception switch
    {
        ApiProblemException p => p,
        LabCodeException l => new ApiProblemException(l.StatusCode, l.Code, "Request refused", l.Message),
        ArgumentException a => new ApiProblemException(400, "validation_error", "Validation failed", a.Message),
        _ => null
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = ToProblem(exception);
        if (problem is null) return false;
        httpContext.Response.StatusCode = problem.Status;
        await httpContext.Response.WriteAsJsonAsync(ApiProblems.Build(httpContext, problem), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        return true;
    }
}

/// <summary>
/// Turns the older { error, code? } bodies some endpoints still return into problem details, so every /api/v2
/// error has the same shape: detail (shown by the apps), title, status, code, traceId, and any extra fields.
/// </summary>
public sealed class ErrorBodyProblemFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: { } value } result || value is ProblemDetails) return;
        var status = result.StatusCode ?? 200;
        if (status < 400) return;
        var type = value.GetType();
        if (type.GetProperty("error")?.GetValue(value) is not string error) return;
        var code = type.GetProperty("code")?.GetValue(value) as string ?? DefaultCode(status);
        var extensions = type.GetProperties().Where(p => p.Name is not ("error" or "code")).ToDictionary(p => p.Name, p => p.GetValue(value));
        var problem = new ApiProblemException(status, code, Title(status), error, extensions);
        context.Result = new ObjectResult(ApiProblems.Build(context.HttpContext, problem)) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    public void OnResultExecuted(ResultExecutedContext context) { }

    private static string DefaultCode(int status) => status switch { 400 => "bad_request", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found", 409 => "conflict", 422 => "unprocessable", _ => "error" };
    private static string Title(int status) => status switch { 400 => "Bad request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not found", 409 => "Conflict", 422 => "Unprocessable", _ => "Error" };
}

/// <summary>A signed-in account that is not Active gets a 403 that says why (account_pending, …), so the apps can show the right screen.</summary>
public sealed class AccountStatusAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        var status = context.User.FindFirstValue(GlazionClaims.AccountStatus);
        if (authorizeResult.Forbidden && context.User.Identity?.IsAuthenticated == true && status is not null && status != nameof(AccountStatus.Active))
        {
            var problem = new ApiProblemException(403, $"account_{status.ToLowerInvariant()}", "Account not active",
                status == nameof(AccountStatus.Pending) ? "Your account is waiting for an administrator to approve it." : "This account is not active.");
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(ApiProblems.Build(context, problem), (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
            return;
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

public static class RateLimits
{
    /// <summary>Sign-in and registration, per client IP.</summary>
    public const string Credentials = "credentials";
}
