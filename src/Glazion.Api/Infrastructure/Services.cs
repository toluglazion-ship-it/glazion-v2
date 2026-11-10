using System.Security.Claims;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Glazion.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Infrastructure;

public sealed class R2Options
{
    public string ServiceUrl { get; set; } = string.Empty;
    public string Region { get; set; } = "auto";
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public int PresignedUploadMinutes { get; set; } = 15;
    public int PresignedReadMinutes { get; set; } = 10;
}

public sealed class UploadOptions
{
    public long MaxBytes { get; set; } = 15 * 1024 * 1024;
    public string[] AllowedContentTypes { get; set; } = ["image/jpeg", "image/png", "image/webp"];
}

public sealed class SessionOptions
{
    /// <summary>Access-token lifetime. Short, because a refresh token renews it.</summary>
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class IngestionOptions
{
    /// <summary>v2 adds pixel checks (blurry, underexposed, overexposed, undecodable_image) from the derivative pass.</summary>
    public string QualityPolicyVersion { get; set; } = "quality-policy-v2";
    public int MinWidth { get; set; } = 1200;
    public int MinHeight { get; set; } = 1200;
    /// <summary>Decompression-bomb guard: width × height above this fails validation before any decoder sees the bytes.</summary>
    public long MaxPixels { get; set; } = 80_000_000;
    public int HeaderBufferBytes { get; set; } = 1024 * 1024;
    public string[] AllowedTileVariants { get; set; } = ["white", "off_white", "pink"];
    public string[] AllowedAtmospheres { get; set; } = ["oxidation", "reduction", "neutral"];
    /// <summary>The dataset's three clay bodies (meeting 2026-10-10): every recipe is tested on each.</summary>
    public string[] AllowedClayBodies { get; set; } = ["porcelain", "stoneware", "red_body"];
    /// <summary>Other spellings that mean an allowed clay body. The apps sent "red" before 2026-10-14.</summary>
    public Dictionary<string, string> ClayBodyAliases { get; set; } = new(StringComparer.Ordinal) { ["red"] = "red_body" };
    /// <summary>
    /// When true, a capture upload without clayBody is refused (Toluwalope, 2026-10-14: compulsory). Off by default so
    /// the Capture app keeps working until it sends the field; turn on with Ingestion__RequireClayBodyAtCapture=true.
    /// </summary>
    public bool RequireClayBodyAtCapture { get; set; }
    public int MaxImportRecordsPerRequest { get; set; } = 200;
    public int ImportHeadConcurrency { get; set; } = 8;

    /// <summary>
    /// Canonical clay body key ("Red Body", "red-body" → "red_body"). Null when nothing was supplied; false when the
    /// value is not one of AllowedClayBodies or a ClayBodyAliases key.
    /// </summary>
    public bool TryNormalizeClayBody(string? value, out string? clayBody)
    {
        clayBody = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var key = string.Join('_', value.Trim().ToLowerInvariant().Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries));
        if (ClayBodyAliases.TryGetValue(key, out var canonical)) key = canonical;
        if (!AllowedClayBodies.Contains(key, StringComparer.Ordinal)) return false;
        clayBody = key;
        return true;
    }
}

public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int Concurrency { get; set; } = 4;
    public int PollIntervalSeconds { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 300;
    public int RetryBaseSeconds { get; set; } = 10;
}

public interface IPasswordService
{
    string Hash(string password);
    bool Verify(string password, string encodedHash);
}

public sealed class PasswordService : IPasswordService
{
    private const int Iterations = 120_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        try
        {
            var parts = encodedHash.Split('.', 4);
            if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public static class TokenService
{
    /// <summary>256-bit random token, URL-safe base64 (no padding).</summary>
    public static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public static class GlazionClaims
{
    public const string SessionId = "sid";
    public const string AccountStatus = "account_status";
}

public static class GlazionPolicies
{
    /// <summary>Any signed-in account, including Pending: /me and sign-out (PRD §6.4 "pending users reach only status screens").</summary>
    public const string AnyAccount = "AnyAccount";
}

public sealed record PresignedUpload(string Url, string ObjectKey, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Headers);
public sealed record StoredObject(long Size, string? ETag, string? ContentType);

public interface IR2ObjectStorage
{
    /// <summary>Signs a PUT for exactly one key and content type. Metadata keys are bare names (e.g. "upload-id"); the x-amz-meta- prefix is added and the header becomes part of the signature, so the client must send it.</summary>
    Task<PresignedUpload> CreatePutUrlAsync(string objectKey, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);
    Task<Uri> CreateGetUrlAsync(string objectKey, CancellationToken cancellationToken);
    /// <summary>Signed GET whose response carries Content-Disposition with the given filename (the lab code), so a saved photograph is named like the tile.</summary>
    Task<Uri> CreateGetUrlAsync(string objectKey, string? downloadFileName, CancellationToken cancellationToken);
    Task<StoredObject?> HeadAsync(string objectKey, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
    Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
    /// <summary>Server-side write of a file the API produced itself (derivatives). Client bytes never go through here.</summary>
    Task PutAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken);
}

public sealed class R2ObjectStorage : IR2ObjectStorage
{
    private const string MetadataPrefix = "x-amz-meta-";
    private readonly IAmazonS3 _client;
    private readonly R2Options _options;

    public R2ObjectStorage(IAmazonS3 client, IOptions<R2Options> options)
    {
        _client = client;
        _options = options.Value;
    }

    public Task<PresignedUpload> CreatePutUrlAsync(string objectKey, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var expires = DateTime.UtcNow.AddMinutes(_options.PresignedUploadMinutes);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expires,
            ContentType = contentType,
            Protocol = PresignProtocol
        };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = contentType };
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
            {
                var bare = key.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase) ? key[MetadataPrefix.Length..] : key;
                request.Metadata[bare] = value;
                headers[MetadataPrefix + bare] = value;
            }
        }
        var url = _client.GetPreSignedURL(request);
        return Task.FromResult(new PresignedUpload(url, objectKey, new DateTimeOffset(expires), headers));
    }

    public Task<Uri> CreateGetUrlAsync(string objectKey, CancellationToken cancellationToken) => CreateGetUrlAsync(objectKey, null, cancellationToken);

    public Task<Uri> CreateGetUrlAsync(string objectKey, string? downloadFileName, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(_options.PresignedReadMinutes),
            Protocol = PresignProtocol
        };
        if (!string.IsNullOrWhiteSpace(downloadFileName))
        {
            var safe = string.Concat(downloadFileName.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
            if (safe.Length > 0) request.ResponseHeaderOverrides.ContentDisposition = $"inline; filename=\"{safe}\"";
        }
        return Task.FromResult(new Uri(_client.GetPreSignedURL(request)));
    }

    public async Task PutAsync(string objectKey, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        using var body = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = objectKey,
            InputStream = body,
            ContentType = contentType,
            Headers = { CacheControl = "private, max-age=86400" },
            // Cloudflare's settings for the .NET SDK: R2 does not accept streaming (chunked) payload signatures or
            // the SDK's default trailing checksums. Unsigned payloads are only allowed over TLS.
            UseChunkEncoding = false,
            DisablePayloadSigning = _options.ServiceUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true,
            DisableDefaultChecksumValidation = true
        }, cancellationToken);
    }

    public async Task<StoredObject?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        try
        {
            var result = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = objectKey }, cancellationToken);
            return new StoredObject(result.ContentLength, result.ETag, result.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _options.Bucket, Key = objectKey }, cancellationToken);
        return response.ResponseStream;
    }

    public async Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await _client.CopyObjectAsync(new CopyObjectRequest { SourceBucket = _options.Bucket, SourceKey = sourceKey, DestinationBucket = _options.Bucket, DestinationKey = destinationKey }, cancellationToken);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Bucket, Key = objectKey }, cancellationToken);
    }

    /// <summary>Cloudflare R2 is always HTTPS; plain HTTP is only honoured for a local S3 emulator configured with an http:// service URL.</summary>
    private Protocol PresignProtocol => _options.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ServiceUrl) || string.IsNullOrWhiteSpace(_options.AccessKey) || string.IsNullOrWhiteSpace(_options.SecretKey) || string.IsNullOrWhiteSpace(_options.Bucket))
            throw new InvalidOperationException("R2 is not configured. Set R2__ServiceUrl, R2__AccessKey, R2__SecretKey, and R2__Bucket.");
    }
}

/// <summary>
/// Opaque bearer tokens backed by UserSessions. Suspended and disabled accounts fail authentication outright;
/// pending accounts authenticate with account_status=Pending, which the default policy refuses everywhere but /me.
/// </summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly GlazionDbContext _db;

    public SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder, GlazionDbContext db)
        : base(options, logger, encoder) => _db = db;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) || !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var raw = header.ToString()[7..].Trim();
        if (raw.Length < 20) return AuthenticateResult.Fail("Invalid session token");
        var hash = TokenService.HashToken(raw);
        var session = await _db.Sessions.AsNoTracking().Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAt == null && x.ExpiresAt > DateTimeOffset.UtcNow, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid or expired session");
        if (session.User.Status is Domain.AccountStatus.Suspended or Domain.AccountStatus.Disabled) return AuthenticateResult.Fail("Account is not active");

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, session.User.Id.ToString()),
            new Claim(ClaimTypes.Name, session.User.Username),
            new Claim(ClaimTypes.Role, session.User.Role.ToString()),
            new Claim(GlazionClaims.SessionId, session.Id.ToString()),
            new Claim(GlazionClaims.AccountStatus, session.User.Status.ToString())
        };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}
