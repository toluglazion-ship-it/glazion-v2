using System.Text;
using System.Text.Json;
using Glazion.Api.Infrastructure;

namespace Glazion.Api.Tests;

public sealed class ObjectKeyPolicyTests
{
    [Fact]
    public void Upload_key_follows_spec_layout_and_derives_extension_from_content_type()
    {
        var id = Guid.Parse("c1f9f088-4d17-4e65-b83a-5ced9d1a1a6e");
        Assert.Equal("prod/originals/default/uploads/c1f9f088-4d17-4e65-b83a-5ced9d1a1a6e/source.jpg", ObjectKeyPolicy.UploadOriginal(id, "image/jpeg"));
        Assert.Equal("prod/originals/default/uploads/c1f9f088-4d17-4e65-b83a-5ced9d1a1a6e/source.webp", ObjectKeyPolicy.UploadOriginal(id, "IMAGE/WEBP"));
    }

    [Fact]
    public void Import_key_is_batch_and_external_record_scoped()
    {
        var batch = Guid.Parse("bf14e6dc-85cc-44a8-b3fa-33b2c5db4d9a");
        Assert.Equal("prod/originals/default/imports/bf14e6dc-85cc-44a8-b3fa-33b2c5db4d9a/dataset-000123/source.png", ObjectKeyPolicy.ImportOriginal(batch, "dataset-000123", "image/png"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("slash/inside")]
    public void Import_key_rejects_unsafe_external_ids(string externalId)
    {
        Assert.False(ObjectKeyPolicy.IsValidExternalRecordId(externalId));
        Assert.Throws<ArgumentException>(() => ObjectKeyPolicy.ImportOriginal(Guid.NewGuid(), externalId, "image/jpeg"));
    }

    [Fact]
    public void Unsupported_content_type_cannot_produce_a_key()
        => Assert.Throws<ArgumentException>(() => ObjectKeyPolicy.UploadOriginal(Guid.NewGuid(), "application/pdf"));

    [Fact]
    public void Quarantine_mirrors_the_original_path()
    {
        var original = ObjectKeyPolicy.ImportOriginal(Guid.Empty, "r1", "image/jpeg");
        Assert.Equal("prod/quarantine/default/imports/00000000-0000-0000-0000-000000000000/r1/source.jpg", ObjectKeyPolicy.Quarantine(original));
    }

    [Fact]
    public void Derivatives_are_keyed_by_image_id()
    {
        var imageId = Guid.Parse("15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0");
        Assert.Equal("prod/derivatives/default/images/15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0/display.webp", ObjectKeyPolicy.Display(imageId));
        Assert.Equal("prod/derivatives/default/images/15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0/thumbnail.webp", ObjectKeyPolicy.Thumbnail(imageId));
    }
}

public sealed class ImageHeaderReaderTests
{
    [Fact]
    public void Reads_png_dimensions_from_ihdr()
    {
        var bytes = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange([0, 0, 0x0B, 0xB8]); // 3000
        bytes.AddRange([0, 0, 0x07, 0xD0]); // 2000
        bytes.AddRange([8, 6, 0, 0, 0]);
        var header = ImageHeaderReader.TryRead(bytes.ToArray());
        Assert.NotNull(header);
        Assert.Equal(("png", "image/png", 3000, 2000), (header.Format, header.ContentType, header.Width, header.Height));
    }

    [Fact]
    public void Reads_jpeg_dimensions_from_sof_after_app_segments()
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        bytes.AddRange([0xFF, 0xE1, 0x00, 0x08, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00]); // APP1 with 6 payload bytes
        bytes.AddRange([0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00]);                         // DQT
        bytes.AddRange([0xFF, 0xC2, 0x00, 0x11, 0x08, 0x06, 0x40, 0x04, 0xB0, 0x03]);  // SOF2: height 1600, width 1200
        var header = ImageHeaderReader.TryRead(bytes.ToArray());
        Assert.NotNull(header);
        Assert.Equal(("jpeg", 1200, 1600), (header.Format, header.Width, header.Height));
    }

    [Fact]
    public void Reads_webp_vp8x_canvas_size()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("RIFF")); bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange(Encoding.ASCII.GetBytes("WEBPVP8X")); bytes.AddRange([10, 0, 0, 0]);
        bytes.AddRange([0x10, 0, 0, 0]);          // flags
        bytes.AddRange([0xFF, 0x0F, 0x00]);       // width-1 = 4095 -> 4096
        bytes.AddRange([0x3F, 0x08, 0x00]);       // height-1 = 2111 -> 2112
        var header = ImageHeaderReader.TryRead(bytes.ToArray());
        Assert.NotNull(header);
        Assert.Equal(("webp", 4096, 2112), (header.Format, header.Width, header.Height));
    }

    [Fact]
    public void Reads_webp_vp8l_size()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("RIFF")); bytes.AddRange([0, 0, 0, 0]);
        bytes.AddRange(Encoding.ASCII.GetBytes("WEBPVP8L")); bytes.AddRange([0, 0, 0, 0]);
        bytes.Add(0x2F);
        // width-1 = 1199 (14 bits), height-1 = 799 (14 bits)
        uint packed = 1199u | (799u << 14);
        bytes.AddRange(BitConverter.GetBytes(packed));
        var header = ImageHeaderReader.TryRead(bytes.ToArray());
        Assert.NotNull(header);
        Assert.Equal((1200, 800), (header.Width, header.Height));
    }

    [Fact]
    public void Rejects_non_image_bytes_regardless_of_declared_type()
    {
        Assert.Null(ImageHeaderReader.TryRead(Encoding.ASCII.GetBytes("%PDF-1.7 not an image at all........")));
        Assert.Null(ImageHeaderReader.TryRead([0xFF, 0xD8, 0xFF, 0xD9])); // JPEG with no frame header
        Assert.Null(ImageHeaderReader.TryRead([]));
    }
}

public sealed class ChemistrySchemaTests
{
    [Fact]
    public void Oxide_order_matches_the_versioned_spec()
    {
        Assert.Equal("oxide-vector-v1", ChemistrySchema.Version);
        Assert.Equal(["Li2O", "Na2O", "K2O", "MgO", "CaO", "SrO", "BaO", "ZnO", "PbO", "B2O3", "Al2O3", "SiO2", "TiO2", "ZrO2", "SnO2", "Fe2O3", "Cr2O3", "CuO", "CoO", "MnO", "NiO", "P2O5"], ChemistrySchema.OxideOrder);
    }

    [Fact]
    public void Valid_umf_is_canonicalised_in_fixed_order_with_stable_hash()
    {
        var a = ChemistrySchema.ValidateUmf(JsonDocument.Parse("""{"SiO2":3.2,"na2o":0.18,"K2O":0.12,"CaO":0.52,"MgO":0.18,"Al2O3":0.35,"B2O3":0.20}""").RootElement);
        var b = ChemistrySchema.ValidateUmf(JsonDocument.Parse("""{"Al2O3":0.350,"Na2O":0.18,"CaO":0.52,"K2O":0.12,"MgO":0.18,"B2O3":0.2,"SiO2":3.20}""").RootElement);
        Assert.True(a.IsValid, string.Join(",", a.Errors));
        Assert.Equal("""{"Na2O":0.18,"K2O":0.12,"MgO":0.18,"CaO":0.52,"B2O3":0.2,"Al2O3":0.35,"SiO2":3.2}""", a.CanonicalJson);
        Assert.Equal(a.InputHash, b.InputHash);
        Assert.Equal(1.00m, a.FluxTotal);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Unknown_oxide_and_bad_values_are_rejected_not_dropped()
    {
        var result = ChemistrySchema.ValidateUmf(JsonDocument.Parse("""{"SiO2":3.2,"CaO":1.0,"Unobtainium":0.1,"MgO":-1}""").RootElement);
        Assert.False(result.IsValid);
        Assert.Contains("unknown_oxide:Unobtainium", result.Errors);
        Assert.Contains("invalid_oxide_value:MgO", result.Errors);
    }

    [Fact]
    public void Non_unity_flux_is_a_warning_and_zero_flux_is_an_error()
    {
        var warn = ChemistrySchema.ValidateUmf(JsonDocument.Parse("""{"SiO2":3.2,"CaO":0.7,"Al2O3":0.3}""").RootElement);
        Assert.True(warn.IsValid);
        Assert.Contains("flux_total_not_unity", warn.Warnings);

        var zero = ChemistrySchema.ValidateUmf(JsonDocument.Parse("""{"SiO2":3.2,"Al2O3":0.3}""").RootElement);
        Assert.False(zero.IsValid);
        Assert.Contains("zero_flux", zero.Errors);
    }

    [Fact]
    public void Absent_oxides_are_not_written_as_zero()
    {
        var result = ChemistrySchema.ValidateUmf(JsonDocument.Parse("""{"CaO":1.0,"SiO2":2.5}""").RootElement);
        Assert.DoesNotContain("Al2O3", result.CanonicalJson);
        Assert.Equal(2, result.Umf.Count);
    }
}

public sealed class JobQueueTests
{
    [Fact]
    public void Dedupe_key_is_job_type_and_entity_scoped()
    {
        var id = Guid.Parse("15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0");
        Assert.Equal("image.validate:15f1e6ad-9a78-4b1b-9e10-90a78c0d9ef0", JobQueue.DedupeKeyFor(JobTypes.ImageValidate, id));
    }
}
