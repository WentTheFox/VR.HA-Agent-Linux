using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using VRHAAgent.Protocol;

namespace VRHAAgent.VR;

/// <summary>Tightly packed, top-down RGBA8 pixel data, as OpenVR expects.</summary>
public sealed class Rgba32Image(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
}

public static class Images
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Loads the image referenced by a payload (inline base64, local path or URL), or null if none.</summary>
    public static async Task<Image<Rgba32>?> LoadAsync(Payload payload)
    {
        if (!string.IsNullOrEmpty(payload.imageData))
            return Image.Load<Rgba32>(Convert.FromBase64String(StripDataUri(payload.imageData)));
        if (!string.IsNullOrEmpty(payload.imagePath))
            return await Image.LoadAsync<Rgba32>(payload.imagePath);
        if (!string.IsNullOrEmpty(payload.imageUrl))
            return Image.Load<Rgba32>(await Http.GetByteArrayAsync(payload.imageUrl));
        return null;
    }

    private static string StripDataUri(string data)
    {
        var comma = data.IndexOf(',');
        return data.StartsWith("data:", StringComparison.Ordinal) && comma >= 0 ? data[(comma + 1)..] : data;
    }

    public static Rgba32Image ToRaw(Image<Rgba32> image, int maxDimension)
    {
        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxDimension, maxDimension),
            }));
        }

        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return new Rgba32Image(image.Width, image.Height, pixels);
    }

    public static string ToPngBase64(Image<Rgba32> image, int maxDimension)
    {
        using var copy = image.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Max,
            Size = new Size(Math.Min(maxDimension, image.Width), Math.Min(maxDimension, image.Height)),
        }));
        return copy.ToBase64String(SixLabors.ImageSharp.Formats.Png.PngFormat.Instance).Split(',', 2)[1];
    }

    public static void DrawTextAreas(Image<Rgba32> image, IEnumerable<Payload.TextArea> textAreas)
    {
        foreach (var area in textAreas)
        {
            if (string.IsNullOrEmpty(area.text)) continue;

            var font = ResolveFont(area.fontFamily, Math.Max(1, area.fontSizePt));
            var color = ParseColor(area.fontColor);
            var x = Math.Clamp(area.xPositionPx, 0, image.Width);
            var y = Math.Clamp(area.yPositionPx, 0, image.Height);
            var width = Math.Min(area.widthPx, image.Width);
            var height = Math.Min(area.heightPx, image.Height);

            var horizontal = area.horizontalAlignment switch
            {
                1 => HorizontalAlignment.Center,
                2 => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            };
            var vertical = area.verticalAlignment switch
            {
                1 => VerticalAlignment.Center,
                2 => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top,
            };

            // Origin is the anchor point inside the box matching the chosen alignment.
            var origin = new PointF(
                x + horizontal switch { HorizontalAlignment.Center => width / 2f, HorizontalAlignment.Right => width, _ => 0 },
                y + vertical switch { VerticalAlignment.Center => height / 2f, VerticalAlignment.Bottom => height, _ => 0 });

            var options = new RichTextOptions(font)
            {
                Origin = origin,
                WrappingLength = width,
                HorizontalAlignment = horizontal,
                VerticalAlignment = vertical,
                TextAlignment = horizontal switch
                {
                    HorizontalAlignment.Center => TextAlignment.Center,
                    HorizontalAlignment.Right => TextAlignment.End,
                    _ => TextAlignment.Start,
                },
                // Font sizes are given in points like System.Drawing did; ImageSharp works in pixels at 72 DPI.
                Dpi = 96,
            };

            var clip = new RectangleF(x, y, width, height);
            image.Mutate(ctx => ctx.Clip(new SixLabors.ImageSharp.Drawing.RectangularPolygon(clip),
                c => c.DrawText(options, area.text, color)));
        }
    }

    private static Font ResolveFont(string family, float size)
    {
        if (!string.IsNullOrWhiteSpace(family) && SystemFonts.TryGet(family, out var requested))
            return requested.CreateFont(size);

        foreach (var fallback in new[] { "Noto Sans", "DejaVu Sans", "Liberation Sans", "Cantarell", "Arial" })
        {
            if (SystemFonts.TryGet(fallback, out var f)) return f.CreateFont(size);
        }

        return SystemFonts.Families.First().CreateFont(size);
    }

    private static Color ParseColor(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var color)) return color;
        return Color.White;
    }
}
