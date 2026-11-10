using Glazion.Api.Contracts;
using Glazion.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Glazion.Api.Controllers;

[ApiController]
[Route("api/v2/auth")]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Signs in. Pending accounts get tokens too; the apps read accountStatus and show the waiting screen.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Credentials)]
    public async Task<ActionResult<LoginResult>> Login(LoginCommand request, CancellationToken cancellationToken) =>
        Ok(await auth.LoginAsync(request.Username ?? string.Empty, request.Password ?? string.Empty, cancellationToken));

    /// <summary>Exchanges a refresh token for a new pair. The old refresh token stops working.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthTokensResult>> Refresh(RefreshCommand request, CancellationToken cancellationToken) =>
        Ok(await auth.RefreshAsync(request.RefreshToken ?? string.Empty, cancellationToken));

    /// <summary>Ends this session only; other devices stay signed in.</summary>
    [HttpPost("logout")]
    [Authorize(Policy = GlazionPolicies.AnyAccount)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (User.GetSessionId() is { } sessionId) await auth.LogoutAsync(sessionId, cancellationToken);
        return NoContent();
    }

    /// <summary>Self-registration (AUTH-02). The account starts Pending until an administrator activates it.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimits.Credentials)]
    public async Task<ActionResult<RegisterResult>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }
}
