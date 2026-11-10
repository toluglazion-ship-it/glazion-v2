using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Controllers;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class AuthServiceTests
{
    private readonly GlazionDbContext _db = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("auth-" + Guid.NewGuid()).Options);
    private readonly PasswordService _passwords = new();
    private AuthService Auth => new(_db, _passwords, Options.Create(new Infrastructure.SessionOptions { AccessTokenMinutes = 15, RefreshTokenDays = 7 }), NullLogger<AuthService>.Instance);

    private async Task<AppUser> AddUserAsync(string username, AccountStatus status = AccountStatus.Active)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = username, DisplayName = username, PasswordHash = _passwords.Hash("correct-horse"), Role = UserRole.User, Status = status };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Login_returns_token_pair_and_account_details()
    {
        var user = await AddUserAsync("potter");
        var result = await Auth.LoginAsync("potter", "correct-horse", default);

        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(AccountStatus.Active, result.AccountStatus);
        Assert.Equal(15 * 60, result.Tokens.ExpiresInSeconds);
        Assert.NotEqual(result.Tokens.AccessToken, result.Tokens.RefreshToken);
        var session = await _db.Sessions.SingleAsync();
        Assert.Equal(TokenService.HashToken(result.Tokens.AccessToken), session.TokenHash);
        Assert.Equal(TokenService.HashToken(result.Tokens.RefreshToken), session.RefreshTokenHash);
    }

    [Fact]
    public async Task An_admin_set_password_replaces_the_old_one_and_signs_the_user_out()
    {
        var user = await AddUserAsync("potter");
        var before = await Auth.LoginAsync("potter", "correct-horse", default);
        await Auth.LoginAsync("potter", "correct-horse", default);

        Assert.Equal(2, await Auth.SetPasswordAsync(user.Id, "new-glaze-pass", default));
        Assert.All(await _db.Sessions.ToListAsync(), s => Assert.NotNull(s.RevokedAt));
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.RefreshAsync(before.Tokens.RefreshToken, default))).Status);
        Assert.Equal("invalid_credentials", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.LoginAsync("potter", "correct-horse", default))).Code);
        Assert.Equal(user.Id, (await Auth.LoginAsync("potter", "new-glaze-pass", default)).UserId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task A_set_password_must_be_8_to_256_characters(string? password)
    {
        var user = await AddUserAsync("potter");
        var hash = user.PasswordHash;
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.SetPasswordAsync(user.Id, password, default))).Status);
        Assert.Equal(hash, (await _db.Users.SingleAsync()).PasswordHash);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.SetPasswordAsync(Guid.NewGuid(), "long-enough-pass", default))).Status);
    }

    [Fact]
    public async Task Wrong_password_is_401_invalid_credentials()
    {
        await AddUserAsync("potter");
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => Auth.LoginAsync("potter", "wrong-password", default));
        Assert.Equal((401, "invalid_credentials"), (ex.Status, ex.Code));
    }

    [Fact]
    public async Task Pending_accounts_sign_in_but_suspended_and_disabled_do_not()
    {
        await AddUserAsync("waiting", AccountStatus.Pending);
        await AddUserAsync("paused", AccountStatus.Suspended);
        await AddUserAsync("gone", AccountStatus.Disabled);

        Assert.Equal(AccountStatus.Pending, (await Auth.LoginAsync("waiting", "correct-horse", default)).AccountStatus);
        Assert.Equal("account_suspended", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.LoginAsync("paused", "correct-horse", default))).Code);
        Assert.Equal("account_disabled", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.LoginAsync("gone", "correct-horse", default))).Code);
    }

    [Fact]
    public async Task Refresh_rotates_and_the_old_refresh_token_stops_working()
    {
        await AddUserAsync("potter");
        var login = await Auth.LoginAsync("potter", "correct-horse", default);

        var next = await Auth.RefreshAsync(login.Tokens.RefreshToken, default);

        Assert.NotEqual(login.Tokens.RefreshToken, next.RefreshToken);
        var sessions = await _db.Sessions.OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.NotNull(sessions[0].RevokedAt);
        Assert.Equal(sessions[1].Id, sessions[0].ReplacedBySessionId);
        Assert.Null(sessions[1].RevokedAt);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_every_session()
    {
        await AddUserAsync("potter");
        var login = await Auth.LoginAsync("potter", "correct-horse", default);
        await Auth.RefreshAsync(login.Tokens.RefreshToken, default);

        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => Auth.RefreshAsync(login.Tokens.RefreshToken, default));

        Assert.Equal((401, "invalid_refresh_token"), (ex.Status, ex.Code));
        Assert.All(await _db.Sessions.ToListAsync(), s => Assert.NotNull(s.RevokedAt));
    }

    [Fact]
    public async Task Refresh_is_refused_for_a_suspended_account()
    {
        var user = await AddUserAsync("potter");
        var login = await Auth.LoginAsync("potter", "correct-horse", default);
        user.Status = AccountStatus.Suspended;
        await _db.SaveChangesAsync();

        Assert.Equal("account_suspended", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.RefreshAsync(login.Tokens.RefreshToken, default))).Code);
    }

    [Fact]
    public async Task Logout_ends_only_the_current_session()
    {
        await AddUserAsync("potter");
        await Auth.LoginAsync("potter", "correct-horse", default);
        await Auth.LoginAsync("potter", "correct-horse", default);
        var first = await _db.Sessions.OrderBy(x => x.CreatedAt).FirstAsync();

        await Auth.LogoutAsync(first.Id, default);

        Assert.Equal(1, await _db.Sessions.CountAsync(x => x.RevokedAt == null));
    }

    [Fact]
    public async Task Registration_creates_a_pending_account_and_refuses_duplicates()
    {
        var result = await Auth.RegisterAsync(new RegisterRequest("Ada Potter", "ada", "Ada@Example.com", "long-enough-password"), default);

        Assert.Equal(AccountStatus.Pending, result.AccountStatus);
        Assert.Equal("ada@example.com", (await _db.Users.SingleAsync(x => x.Id == result.UserId)).Email);
        Assert.Equal("username_taken", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.RegisterAsync(new RegisterRequest("A", "ada", "other@example.com", "long-enough-password"), default))).Code);
        Assert.Equal("email_taken", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.RegisterAsync(new RegisterRequest("A", "ada2", "ADA@example.com", "long-enough-password"), default))).Code);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => Auth.RegisterAsync(new RegisterRequest("A", "ada3", "not-an-email", "long-enough-password"), default))).Code);
    }
}

public sealed class V2ContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Numeric values from @glazion/types (packages/types/src/index.ts). The apps compare these numbers directly.</summary>
    [Fact]
    public void Enum_numbers_match_the_client_types()
    {
        Assert.Equal([0, 1, 2, 3], new[] { AccountStatus.Pending, AccountStatus.Active, AccountStatus.Suspended, AccountStatus.Disabled }.Select(x => (int)x));
        Assert.Equal([0, 1], new[] { UserRole.Admin, UserRole.User }.Select(x => (int)x));
        Assert.Equal(Enumerable.Range(0, 11), new[]
        {
            UploadStatus.Created, UploadStatus.AwaitingUpload, UploadStatus.Uploaded, UploadStatus.Validating, UploadStatus.NeedsReview, UploadStatus.Approved,
            UploadStatus.Rejected, UploadStatus.Indexing, UploadStatus.Indexed, UploadStatus.Failed, UploadStatus.Cancelled
        }.Select(x => (int)x));
    }

    [Fact]
    public void Login_result_serialises_with_the_client_field_names_and_numeric_enums()
    {
        var json = JsonSerializer.Serialize(new LoginResult(new AuthTokensResult("a", "r", 3600), Guid.Empty, "potter", "Potter", UserRole.User, AccountStatus.Pending), Web);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("a", root.GetProperty("tokens").GetProperty("accessToken").GetString());
        Assert.Equal(3600, root.GetProperty("tokens").GetProperty("expiresInSeconds").GetInt32());
        Assert.Equal(1, root.GetProperty("role").GetInt32());
        Assert.Equal(0, root.GetProperty("accountStatus").GetInt32());
        Assert.Equal("potter", root.GetProperty("username").GetString());
    }

    [Fact]
    public void Paged_result_and_cursor_round_trip()
    {
        var json = JsonSerializer.Serialize(new PagedResult<int>([1, 2], 5, Paging.Encode(2)), Web);
        Assert.Contains("\"items\":[1,2]", json);
        Assert.Contains("\"totalCount\":5", json);
        Assert.Equal(2, Paging.Decode(Paging.Encode(2)));
        Assert.Equal(0, Paging.Decode(null));
        Assert.Equal(1234, Paging.Decode(Paging.Encode(1234)));
        Assert.Throws<ApiProblemException>(() => Paging.Decode("not a cursor!"));
    }

    [Fact]
    public async Task Paging_walks_every_row_exactly_once()
    {
        using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("paging-" + Guid.NewGuid()).Options);
        for (var i = 0; i < 7; i++) db.Recipes.Add(new Recipe { Id = Guid.NewGuid(), Name = $"R{i}", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
        await db.SaveChangesAsync();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Paging.PageAsync(db.Recipes.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id), cursor, 3,
                rows => Task.FromResult<IReadOnlyList<string>>(rows.Select(x => x.Name).ToList()), default);
            Assert.Equal(7, page.TotalCount);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"R{i}"), seen);
    }

    [Fact]
    public void Capture_codes_are_random_hashed_and_masked()
    {
        var code = CaptureCodeGenerator.Create("lab-a");
        Assert.Matches("^GLZ-LAB-A-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
        Assert.Matches("^GLZ-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", CaptureCodeGenerator.Create(null));
        Assert.NotEqual(code, CaptureCodeGenerator.Create("lab-a"));
        Assert.Equal(code[^4..], CaptureCodeGenerator.Suffix(code));
        Assert.Equal(CaptureCodeGenerator.Hash(code), CaptureCodeGenerator.Hash(" " + code.ToLowerInvariant() + " "));
        Assert.True(CaptureCodeGenerator.IsValidLabelPrefix("LAB-A"));
        Assert.False(CaptureCodeGenerator.IsValidLabelPrefix("LAB A"));
    }

    [Theory]
    [InlineData("image/jpeg", "x.png", "image/jpeg")]
    [InlineData("IMAGE/JPG", null, "image/jpeg")]
    [InlineData("image/webp; q=1", null, "image/webp")]
    [InlineData("", "tile.PNG", "image/png")]
    [InlineData(null, "tile.jpeg", "image/jpeg")]
    [InlineData(null, "tile", null)]
    public void Upload_content_type_falls_back_to_the_file_extension(string? declared, string? fileName, string? expected) =>
        Assert.Equal(expected, UploadContentTypes.Resolve(declared, fileName));

    [Fact]
    public void Recipe_dto_reports_no_recipe_json_when_none_is_attached()
    {
        Assert.Null(Views.Recipe(new Recipe { Name = "Plain", RecipeJson = "{}" }, null).RecipeJson);
        Assert.Equal("{\"a\":1}", Views.Recipe(new Recipe { Name = "Rich", RecipeJson = "{\"a\":1}" }, null).RecipeJson);
    }

    [Fact]
    public void Revoked_and_expired_capture_codes_are_not_usable()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(Views.IsUsable(new CaptureCode { IsActive = true }, now));
        Assert.False(Views.IsUsable(new CaptureCode { IsActive = true, RevokedAt = now }, now));
        Assert.False(Views.IsUsable(new CaptureCode { IsActive = true, ExpiresAt = now.AddSeconds(-1) }, now));
        Assert.False(Views.IsUsable(new CaptureCode { IsActive = false }, now));
    }
}
