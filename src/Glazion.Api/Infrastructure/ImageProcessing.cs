using SkiaSharp;

namespace Glazion.Api.Infrastructure;

/// <summary>Derivative sizes and the quality thresholds that send an image to review (never to failure).</summary>
public sealed class DerivativeOptions
{
    public bool Enabled { get; set; } = true;
    public int DisplayMaxEdge { get; set; } = 2048;
    public int ThumbnailMaxEdge { get; set; } = 400;
    public int DisplayQuality { get; set; } = 82;
    public int ThumbnailQuality { get; set; } = 75;
    /// <summary>
    /// Originals whose long edge is at most this get lossless derivatives. Such a photo has no detail to spare (most of
    /// the imported corpus is 113 px), and lossless it is still only tens of kilobytes; lossy at the qualities above,
    /// a 14 KB PNG became a blurred 1.4 KB WebP. Larger originals keep lossy derivatives.
    /// </summary>
    public int LosslessMaxEdge { get; set; } = 400;
    /// <summary>Originals larger than this are not decoded (derivatives skipped, reason <c>derivatives_skipped_too_large</c>).</summary>
    public long MaxDecodeBytes { get; set; } = 64L * 1024 * 1024;
    /// <summary>Long edge of the grey image the metrics are computed on, so scores do not depend on camera resolution.</summary>
    public int MetricsEdge { get; set; } = 1024;
    /// <summary>Variance of the Laplacian below this is <c>blurry</c>.</summary>
    public double MinSharpness { get; set; } = 40;
    /// <summary>Mean luma (0–255) below this, or more than <see cref="MaxDarkFraction"/> of pixels near black, is <c>underexposed</c>.</summary>
    public double MinMeanLuma { get; set; } = 45;
    public double MaxDarkFraction { get; set; } = 0.5;
    /// <summary>More than this share of pixels clipped to white is <c>overexposed</c>. Test tiles are white, so only heavy clipping counts.</summary>
    public double MaxClippedFraction { get; set; } = 0.3;
}

/// <param name="Sharpness">Variance of the 4-neighbour Laplacian of luma at <see cref="DerivativeOptions.MetricsEdge"/>.</param>
/// <param name="DarkFraction">Share of pixels with luma ≤ 8.</param>
/// <param name="ClippedFraction">Share of pixels with luma ≥ 250.</param>
public sealed record ImageQualityMetrics(double Sharpness, double MeanLuma, double DarkFraction, double ClippedFraction, int Width, int Height, string Orientation);

public sealed record ProcessedImage(byte[] Display, byte[] Thumbnail, ImageQualityMetrics Metrics, IReadOnlyList<string> ReviewReasons);

public sealed class ImageDecodeException(string message) : Exception(message);

/// <summary>
/// Decodes an original once and produces everything that needs pixels: the upright display and thumbnail WebP
/// derivatives (metadata such as GPS is not carried over) and the quality metrics. Pure: no storage, no database.
/// </summary>
public static class ImageProcessor
{
    public const string BlurryReason = "blurry";
    public const string UnderexposedReason = "underexposed";
    public const string OverexposedReason = "overexposed";

    public static ProcessedImage Process(byte[] original, DerivativeOptions options)
    {
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data) ?? throw new ImageDecodeException("The image could not be decoded.");
        var origin = codec.EncodedOrigin;
        var stored = codec.Info;
        var swap = SwapsAxes(origin);
        var uprightLong = Math.Max(stored.Width, stored.Height);

        // Decode at the smallest size the codec can produce that is still at least the display size (JPEG decodes
        // at 1/2, 1/4, 1/8 natively), so a 48 MP photo never becomes a 48 MP bitmap.
        var scale = Math.Min(1f, (float)options.DisplayMaxEdge / uprightLong);
        var decodeSize = codec.GetScaledDimensions(scale);
        if (Math.Max(decodeSize.Width, decodeSize.Height) < Math.Min(options.DisplayMaxEdge, uprightLong)) decodeSize = new SKSizeI(stored.Width, stored.Height);
        var info = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new ImageDecodeException($"The image could not be decoded ({result}).");

        using var upright = Orient(decoded, origin);
        using var display = Fit(upright, options.DisplayMaxEdge);
        using var thumbnail = Fit(display, options.ThumbnailMaxEdge);
        using var metricsImage = Fit(display, options.MetricsEdge);

        var uprightWidth = swap ? stored.Height : stored.Width;
        var uprightHeight = swap ? stored.Width : stored.Height;
        var metrics = Measure(metricsImage, uprightWidth, uprightHeight, origin.ToString());
        var lossless = uprightLong <= options.LosslessMaxEdge;
        return new ProcessedImage(Encode(display, options.DisplayQuality, lossless), Encode(thumbnail, options.ThumbnailQuality, lossless), metrics, ReasonsFor(metrics, options));
    }

    public static IReadOnlyList<string> ReasonsFor(ImageQualityMetrics m, DerivativeOptions o)
    {
        var reasons = new List<string>();
        if (m.Sharpness < o.MinSharpness) reasons.Add(BlurryReason);
        if (m.MeanLuma < o.MinMeanLuma || m.DarkFraction > o.MaxDarkFraction) reasons.Add(UnderexposedReason);
        if (m.ClippedFraction > o.MaxClippedFraction) reasons.Add(OverexposedReason);
        return reasons;
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>Applies the EXIF orientation so derivatives are upright regardless of how the phone stored them.</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = SwapsAxes(origin);
        var (w, h) = swap ? (source.Height, source.Width) : (source.Width, source.Height);
        var target = new SKBitmap(new SKImageInfo(w, h, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(target);
        var sw = source.Width; var sh = source.Height;
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, sw, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, sw, 0, -1, sh, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, sh, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, sh, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, sh, -1, 0, sw, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, sw, 0, 0, 1),
            _ => SKMatrix.Identity
        };
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return target;
    }

    /// <summary>Scales down so the long edge is at most <paramref name="maxEdge"/>; never scales up.</summary>
    private static SKBitmap Fit(SKBitmap source, int maxEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxEdge) return source.Copy();
        var factor = (double)maxEdge / longEdge;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * factor)), Math.Max(1, (int)Math.Round(source.Height * factor)), source.ColorType, source.AlphaType);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? throw new ImageDecodeException("The image could not be resized.");
    }

    /// <summary>WebP, lossy at <paramref name="quality"/>, or lossless (where quality sets only the encoder's effort).</summary>
    private static byte[] Encode(SKBitmap bitmap, int quality, bool lossless)
    {
        using var pixmap = bitmap.PeekPixels();
        var options = new SKWebpEncoderOptions(lossless ? SKWebpEncoderCompression.Lossless : SKWebpEncoderCompression.Lossy, quality);
        using var encoded = pixmap.Encode(options) ?? throw new ImageDecodeException("WebP encoding failed.");
        return encoded.ToArray();
    }

    /// <summary>Luma statistics and the variance of the Laplacian (a standard focus measure: low variance, few edges, blur).</summary>
    public static ImageQualityMetrics Measure(SKBitmap bitmap, int width, int height, string orientation)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var luma = new float[w * h];
        var pixels = bitmap.Pixels;
        double sum = 0; long dark = 0, clipped = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var y = 0.299f * p.Red + 0.587f * p.Green + 0.114f * p.Blue;
            luma[i] = y;
            sum += y;
            if (y <= 8) dark++;
            if (y >= 250) clipped++;
        }
        double lapSum = 0, lapSq = 0; long n = 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                double lap = luma[i - 1] + luma[i + 1] + luma[i - w] + luma[i + w] - 4 * luma[i];
                lapSum += lap; lapSq += lap * lap; n++;
            }
        var mean = n > 0 ? lapSum / n : 0;
        var variance = n > 0 ? lapSq / n - mean * mean : 0;
        var total = (double)pixels.Length;
        return new ImageQualityMetrics(Math.Round(variance, 2), Math.Round(sum / total, 2), Math.Round(dark / total, 4), Math.Round(clipped / total, 4), width, height, orientation);
    }
}
