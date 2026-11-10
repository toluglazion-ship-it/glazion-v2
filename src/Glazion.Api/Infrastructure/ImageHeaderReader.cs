using System.Buffers.Binary;

namespace Glazion.Api.Infrastructure;

public sealed record ImageHeader(string Format, string ContentType, int Width, int Height);

/// <summary>
/// Reads format and pixel dimensions from the leading bytes of JPEG, PNG, and WebP files using their magic
/// numbers and structural headers. No pixel decoding, no third-party codec; this is the server-side check that
/// the bytes are what the client declared (plan §14) and the input to the decompression-bomb guard.
/// </summary>
public static class ImageHeaderReader
{
    public static ImageHeader? TryRead(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 24 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G') return ReadPng(head);
        if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(head);
        if (head.Length >= 16 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return ReadWebp(head);
        return null;
    }

    private static ImageHeader? ReadPng(ReadOnlySpan<byte> head)
    {
        // Signature (8) + IHDR length (4) + "IHDR" (4) + width (4) + height (4)
        if (head[12] != 'I' || head[13] != 'H' || head[14] != 'D' || head[15] != 'R') return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(head[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(head[20..24]);
        return width <= 0 || height <= 0 ? null : new ImageHeader("png", "image/png", width, height);
    }

    private static ImageHeader? ReadJpeg(ReadOnlySpan<byte> head)
    {
        var i = 2;
        while (i + 4 <= head.Length)
        {
            if (head[i] != 0xFF) { i++; continue; }
            var marker = head[i + 1];
            if (marker == 0xFF) { i++; continue; }           // padding
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { i += 2; continue; } // standalone markers
            if (marker == 0xD9 || marker == 0xDA) return null; // EOI or SOS before any SOF: no frame header
            var length = BinaryPrimitives.ReadUInt16BigEndian(head.Slice(i + 2, 2));
            if (length < 2) return null;
            var isSof = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isSof)
            {
                if (i + 9 > head.Length) return null;
                var height = BinaryPrimitives.ReadUInt16BigEndian(head.Slice(i + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(head.Slice(i + 7, 2));
                return width == 0 || height == 0 ? null : new ImageHeader("jpeg", "image/jpeg", width, height);
            }
            i += 2 + length;
        }
        return null;
    }

    private static ImageHeader? ReadWebp(ReadOnlySpan<byte> head)
    {
        var chunk = head.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Simple lossy: 3-byte frame tag, 3-byte start code (9D 01 2A), then 14-bit width/height
            if (head.Length < 30) return null;
            if (head[23] != 0x9D || head[24] != 0x01 || head[25] != 0x2A) return null;
            var width = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(26, 2)) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(28, 2)) & 0x3FFF;
            return Make(width, height);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Lossless: signature byte 0x2F then 14-bit width-1 and height-1
            if (head.Length < 25 || head[20] != 0x2F) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(21, 4));
            var width = (int)(bits & 0x3FFF) + 1;
            var height = (int)((bits >> 14) & 0x3FFF) + 1;
            return Make(width, height);
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Extended: flags (4) then 24-bit canvas width-1 and height-1
            if (head.Length < 30) return null;
            var width = (head[24] | head[25] << 8 | head[26] << 16) + 1;
            var height = (head[27] | head[28] << 8 | head[29] << 16) + 1;
            return Make(width, height);
        }
        return null;

        static ImageHeader? Make(int width, int height) => width <= 0 || height <= 0 ? null : new ImageHeader("webp", "image/webp", width, height);
    }
}
