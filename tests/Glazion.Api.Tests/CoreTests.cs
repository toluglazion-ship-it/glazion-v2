using Glazion.Api.Contracts;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Password_hash_round_trips_and_rejects_wrong_password()
    {
        var service = new PasswordService();
        var hash = service.Hash("correct-password");

        Assert.NotEqual("correct-password", hash);
        Assert.True(service.Verify("correct-password", hash));
        Assert.False(service.Verify("wrong-password", hash));
    }

    [Fact]
    public void Token_hash_is_deterministic_but_not_plaintext()
    {
        var token = TokenService.CreateToken();
        var hash = TokenService.HashToken(token);

        Assert.Equal(hash, TokenService.HashToken(token));
        Assert.NotEqual(token, hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Upload_validation_accepts_supported_images_within_limit()
    {
        var options = new UploadOptions { MaxBytes = 1000, AllowedContentTypes = ["image/jpeg"] };
        ContractValidation.ValidateUpload("tile.jpg", "image/jpeg", 999, options);
    }

    [Theory]
    [InlineData("application/pdf", 100)]
    [InlineData("image/jpeg", 1001)]
    public void Upload_validation_rejects_unsupported_or_oversized_files(string contentType, long size)
    {
        var options = new UploadOptions { MaxBytes = 1000, AllowedContentTypes = ["image/jpeg"] };
        Assert.Throws<ArgumentException>(() => ContractValidation.ValidateUpload("tile.jpg", contentType, size, options));
    }
}
