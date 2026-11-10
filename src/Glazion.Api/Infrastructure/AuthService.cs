using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Infrastructure;

/// <summary>
/// Sessions with an access token and a rotating refresh token (PRD §13 "rotating refresh tokens"). Tokens are random,
/// returned once, and stored only as SHA-256 hashes. A refresh revokes the session it came from and issues a new one;
/// presenting an already-rotated refresh token again revokes every session of that user (token theft signal).
/// </summary>
public sealed class AuthService(GlazionDbContext db, IPasswordService passwords, IOptions<SessionOptions> options, ILogger<AuthService> logger)
{
    private readonly SessionOptions _options = options.Value;

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        ContractValidation.ValidateCredentials(username, password);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == username.Trim(), cancellationToken);
        if (user is null || !passwords.Verify(password, user.PasswordHash))
            throw new ApiProblemException(401, "invalid_credentials", "Sign-in failed", "Invalid username or password.");
        EnsureCanSignIn(user);
        var tokens = await IssueAsync(user, cancellationToken);
        return new LoginResult(tokens, user.Id, user.Username, user.DisplayName, user.Role, user.Status);
    }

    public async Task<AuthTokensResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw InvalidRefresh();
        var hash = TokenService.HashToken(refreshToken.Trim());
        var session = await db.Sessions.Include(x => x.User).SingleOrDefaultAsync(x => x.RefreshTokenHash == hash, cancellationToken);
        if (session is null) throw InvalidRefresh();

        if (session.RevokedAt is not null)
        {
            if (session.ReplacedBySessionId is not null)
            {
                // A refresh token that was already exchanged is being used again: assume it leaked.
                var revoked = await RevokeAllAsync(session.UserId, cancellationToken);
                logger.LogWarning("Refresh token reuse for user {UserId}; revoked {Count} sessions", session.UserId, revoked);
            }
            throw InvalidRefresh();
        }
        if (session.RefreshExpiresAt is null || session.RefreshExpiresAt <= DateTimeOffset.UtcNow) throw InvalidRefresh();
        EnsureCanSignIn(session.User);

        var now = DateTimeOffset.UtcNow;
        var (next, tokens) = NewSession(session.User, now);
        session.RevokedAt = now;
        session.ReplacedBySessionId = next.Id;
        db.Sessions.Add(next);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw InvalidRefresh(); } // lost a race with a concurrent refresh of the same token
        return tokens;
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.RevokedAt == null, cancellationToken);
        if (session is null) return;
        session.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// An administrator sets a user's password (there is no self-service reset until email is set up). The user is
    /// signed out everywhere, so the old password and any session opened with it stop working. Returns the number of
    /// sessions ended.
    /// </summary>
    public async Task<int> SetPasswordAsync(Guid userId, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8 || password.Length > 256)
            throw ApiProblems.Validation("password", "Password must be between 8 and 256 characters.");
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken) ?? throw ApiProblems.NotFound("User");
        user.PasswordHash = passwords.Hash(password);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await RevokeAllAsync(userId, cancellationToken);
    }

    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var active = await db.Sessions.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            foreach (var s in active) s.RevokedAt = now;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return active.Count;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                // A concurrent refresh rotated one of these sessions; reload and revoke what is live now, including its successor.
                foreach (var s in active) db.Entry(s).State = EntityState.Detached;
            }
        }
    }

    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        ContractValidation.ValidateCredentials(request.Username, request.Password);
        var username = request.Username.Trim();
        var email = request.Email?.Trim();
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            throw ApiProblems.Validation("email", "A valid email address is required.");
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName.Trim();
        if (displayName.Length > 160) throw ApiProblems.Validation("displayName", "Display name must be at most 160 characters.");
        if (await db.Users.AnyAsync(x => x.Username == username, cancellationToken))
            throw new ApiProblemException(409, "username_taken", "Username taken", "That username is already taken.");
        var lowered = email.ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == lowered, cancellationToken))
            throw new ApiProblemException(409, "email_taken", "Email in use", "An account with that email address already exists.");

        // Self-registered accounts wait for an administrator (AUTH-02, PRD §6.4).
        var user = new AppUser { Id = Guid.NewGuid(), Username = username, DisplayName = displayName, Email = lowered, PasswordHash = passwords.Hash(request.Password), Role = UserRole.User, Status = AccountStatus.Pending };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} registered and is pending approval", user.Id);
        return new RegisterResult(user.Id, user.Status);
    }

    private async Task<AuthTokensResult> IssueAsync(AppUser user, CancellationToken cancellationToken)
    {
        var (session, tokens) = NewSession(user, DateTimeOffset.UtcNow);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return tokens;
    }

    private (UserSession Session, AuthTokensResult Tokens) NewSession(AppUser user, DateTimeOffset now)
    {
        var access = TokenService.CreateToken();
        var refresh = TokenService.CreateToken();
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = TokenService.HashToken(access),
            ExpiresAt = now.AddMinutes(_options.AccessTokenMinutes),
            RefreshTokenHash = TokenService.HashToken(refresh),
            RefreshExpiresAt = now.AddDays(_options.RefreshTokenDays),
            CreatedAt = now
        };
        return (session, new AuthTokensResult(access, refresh, _options.AccessTokenMinutes * 60));
    }

    private static void EnsureCanSignIn(AppUser user)
    {
        if (user.Status == AccountStatus.Suspended)
            throw new ApiProblemException(403, "account_suspended", "Account suspended", "This account is suspended. Contact an administrator.");
        if (user.Status == AccountStatus.Disabled)
            throw new ApiProblemException(403, "account_disabled", "Account disabled", "This account has been disabled.");
    }

    private static ApiProblemException InvalidRefresh() =>
        new(401, "invalid_refresh_token", "Session expired", "Your session has expired. Please sign in again.");
}
