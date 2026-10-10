using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace NexusAuth.Host.Authentication;

public sealed record SliderCaptchaImages(
    string BackgroundDataUrl,
    string PieceDataUrl,
    int PieceTop,
    int Width,
    int Height,
    int PieceSize);

public static class SliderCaptchaImageRenderer
{
    private const int Width = 600;
    private const int Height = 360;
    private const int PieceSize = 82;
    private const int TabRadius = 13;
    private const int Padding = 12;
    private static int sceneCounter = RandomNumberGenerator.GetInt32(5);

    public static SliderCaptchaImages Render(int targetOffsetPixels, int trackWidth)
    {
        if (trackWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(trackWidth));
        if (targetOffsetPixels < 0 || targetOffsetPixels > trackWidth)
            throw new ArgumentOutOfRangeException(nameof(targetOffsetPixels));

        var random = RandomNumberGenerator.GetInt32(int.MaxValue);
        var scene = (int)((uint)Interlocked.Increment(ref sceneCounter) % 5);
        var gapLeft = (int)Math.Round(
            targetOffsetPixels / (double)trackWidth * (Width - PieceSize));
        var gapTop = RandomNumberGenerator.GetInt32(Padding + TabRadius + 2, Height - PieceSize - Padding - TabRadius - 2);
        var background = new byte[Width * Height * 3];
        DrawScene(background, scene, random);

        var mask = CreateMask(random);
        var rgba = new byte[PieceSize * PieceSize * 4];
        for (var y = 0; y < PieceSize; y++)
        {
            for (var x = 0; x < PieceSize; x++)
            {
                var maskIndex = y * PieceSize + x;
                if (!mask[maskIndex]) continue;

                var source = ((gapTop + y) * Width + gapLeft + x) * 3;
                var target = maskIndex * 4;
                rgba[target] = background[source];
                rgba[target + 1] = background[source + 1];
                rgba[target + 2] = background[source + 2];
                rgba[target + 3] = 255;
            }
        }

        ShadeGap(background, gapLeft, gapTop, mask);
        return new SliderCaptchaImages(
            ToDataUrl(EncodePng(Width, Height, 3, background)),
            ToDataUrl(EncodePng(PieceSize, PieceSize, 4, rgba)),
            gapTop,
            Width,
            Height,
            PieceSize);
    }

    private static void DrawScene(byte[] pixels, int scene, int seed)
    {
        var palettes = new ((int, int, int) SkyTop, (int, int, int) SkyBottom, (int, int, int) GroundTop, (int, int, int) GroundBottom)[]
        {
            ((37, 52, 105), (242, 151, 110), (68, 68, 116), (35, 42, 76)),
            ((42, 132, 175), (186, 224, 209), (27, 134, 161), (22, 75, 101)),
            ((38, 91, 82), (163, 198, 151), (71, 119, 86), (34, 69, 59)),
            ((81, 130, 174), (245, 198, 137), (197, 153, 103), (117, 83, 65)),
            ((49, 65, 105), (225, 137, 119), (82, 103, 122), (36, 48, 69))
        };
        var palette = palettes[scene];
        var tone = (seed % 25) - 12;
        palette = (
            Shift(palette.SkyTop, tone, tone / 2, -tone / 3),
            Shift(palette.SkyBottom, tone, tone / 2, -tone / 3),
            Shift(palette.GroundTop, tone / 2, -tone / 3, tone),
            Shift(palette.GroundBottom, tone / 2, -tone / 3, tone));
        var horizon = 145 + (seed & 31);
        for (var y = 0; y < Height; y++)
        {
            var upper = y < horizon;
            var start = upper ? palette.SkyTop : palette.GroundTop;
            var end = upper ? palette.SkyBottom : palette.GroundBottom;
            var range = upper ? horizon : Height;
            var amount = Math.Clamp(y / (double)Math.Max(1, range), 0, 1);
            var wave = (int)(Math.Sin((y + seed % 97) * .035) * 3);
            var r = Lerp(start.Item1, end.Item1, amount) + wave;
            var g = Lerp(start.Item2, end.Item2, amount) + wave;
            var b = Lerp(start.Item3, end.Item3, amount) + wave;
            for (var x = 0; x < Width; x++) SetPixel(pixels, x, y, r, g, b);
        }

        switch (scene)
        {
            case 0: DrawMountainSunset(pixels, horizon, seed); break;
            case 1: DrawCoast(pixels, horizon, seed); break;
            case 2: DrawForestLake(pixels, horizon, seed); break;
            case 3: DrawDunes(pixels, horizon, seed); break;
            default: DrawCity(pixels, horizon, seed); break;
        }
        AddAtmosphere(pixels, seed);
    }

    private static void DrawMountainSunset(byte[] pixels, int horizon, int seed)
    {
        var sunX = 95 + seed % 390;
        FillCircle(pixels, sunX, Math.Max(52, horizon - 35), 30 + seed % 14, (255, 216, 151));
        FillPolygon(pixels, [(0, horizon + 42), (110, horizon - 48), (183, horizon + 12), (292, horizon - 76), (430, horizon + 3), (520, horizon - 33), (600, horizon + 25), (600, 360), (0, 360)], (81, 87, 139));
        FillPolygon(pixels, [(0, horizon + 93), (138, horizon + 5), (228, horizon + 88), (376, horizon - 3), (493, horizon + 66), (600, horizon + 21), (600, 360), (0, 360)], (52, 62, 108));
        FillPolygon(pixels, [(272, horizon - 62), (292, horizon - 76), (317, horizon - 46), (299, horizon - 54)], (226, 220, 202));
    }

    private static void DrawCoast(byte[] pixels, int horizon, int seed)
    {
        var horizonLine = horizon + 18;
        FillCircle(pixels, 445 - seed % 90, horizonLine - 22, 25, (255, 235, 184));
        for (var y = horizonLine; y < Height; y++)
        {
            var wave = Math.Sin((y + seed) * .12) * 15 + Math.Sin((y + seed) * .047) * 10;
            var boundary = 365 + wave;
            for (var x = 0; x < Width; x++)
                if (x > boundary) SetPixel(pixels, x, y, 32 + (y % 25), 137 + (y % 28), 160 + (y % 30));
        }
        for (var row = 0; row < 6; row++)
        {
            var y = horizonLine + 22 + row * 25;
            var x = 390 + (seed + row * 31) % 130;
            DrawLine(pixels, x, y, x + 18 + row * 3, y, (164, 222, 217), 2);
        }
        FillPolygon(pixels, [(0, horizonLine + 42), (105, horizonLine + 18), (185, horizonLine + 48), (276, horizonLine + 23), (364, horizonLine + 55), (342, 360), (0, 360)], (222, 183, 128));
    }

    private static void DrawForestLake(byte[] pixels, int horizon, int seed)
    {
        FillPolygon(pixels, [(0, horizon + 2), (103, horizon - 44), (209, horizon + 4), (318, horizon - 61), (458, horizon + 6), (546, horizon - 40), (600, horizon - 6), (600, 360), (0, 360)], (69, 105, 95));
        FillPolygon(pixels, [(0, horizon + 38), (600, horizon + 16), (600, 282), (0, 302)], (66, 143, 147));
        for (var i = 0; i < 35; i++)
        {
            var x = (i * 83 + seed) % Width;
            var baseY = horizon + 8 + (i * 17 % 42);
            var size = 12 + (i * 7 % 20);
            FillPolygon(pixels, [(x, baseY - size * 2), (x - size, baseY), (x + size, baseY)], i % 2 == 0 ? (37, 83, 72) : (53, 109, 83));
            DrawLine(pixels, x, baseY - 3, x, baseY + 17, (82, 72, 56), 4);
        }
        for (var i = 0; i < 9; i++) DrawLine(pixels, 210 + i * 19, 248 + i % 3 * 11, 260 + i * 24, 248 + i % 3 * 11, (143, 197, 177), 2);
    }

    private static void DrawDunes(byte[] pixels, int horizon, int seed)
    {
        FillCircle(pixels, 135 + seed % 310, horizon - 39, 27, (255, 232, 178));
        FillPolygon(pixels, [(0, horizon + 48), (92, horizon + 5), (228, horizon + 38), (365, horizon - 10), (489, horizon + 43), (600, horizon + 3), (600, 360), (0, 360)], (205, 157, 106));
        FillPolygon(pixels, [(0, horizon + 123), (117, horizon + 68), (253, horizon + 109), (396, horizon + 54), (600, horizon + 99), (600, 360), (0, 360)], (174, 123, 88));
        FillPolygon(pixels, [(0, horizon + 190), (147, horizon + 132), (302, horizon + 181), (451, horizon + 119), (600, horizon + 170), (600, 360), (0, 360)], (128, 91, 76));
        for (var i = 0; i < 12; i++) DrawLine(pixels, 40 + i * 41, 260 + (i * 29 % 75), 100 + i * 41, 259 + (i * 29 % 75), (231, 187, 132), 2);
    }

    private static void DrawCity(byte[] pixels, int horizon, int seed)
    {
        FillCircle(pixels, 486 - seed % 110, horizon - 21, 25, (255, 203, 163));
        var x = -12;
        var index = 0;
        while (x < Width)
        {
            var width = 28 + (seed + index * 19) % 32;
            var height = 45 + (seed / 3 + index * 37) % 118;
            var top = horizon + 18 - height;
            var color = index % 2 == 0 ? (57, 75, 104) : (70, 83, 111);
            FillRect(pixels, x, top, width, horizon + 43 - top, color);
            for (var wy = top + 10; wy < horizon + 22; wy += 17)
                for (var wx = x + 7; wx < x + width - 4; wx += 12)
                    FillRect(pixels, wx, wy, 4, 6, (245, 184, 128));
            x += width + 5;
            index++;
        }
        FillRect(pixels, 0, horizon + 43, Width, Height - horizon - 43, (42, 57, 79));
        for (var i = 0; i < 12; i++) DrawLine(pixels, 20 + i * 47, horizon + 87 + i % 3 * 19, 40 + i * 47, horizon + 87 + i % 3 * 19, (198, 149, 118), 2);
    }

    private static void AddAtmosphere(byte[] pixels, int seed)
    {
        for (var i = 0; i < 34; i++)
        {
            var x = (seed + i * 137) % Width;
            var y = (seed / 7 + i * 47) % 126;
            if (i % 3 == 0) FillCircle(pixels, x, y, 1 + i % 2, (255, 245, 217));
        }
        for (var y = 0; y < Height; y++)
        {
            var edge = Math.Min(Math.Min(y, Height - y), Math.Min(Width / 2, Math.Min(250, Width - 1)));
            var shade = Math.Max(0, 24 - edge / 7);
            if (shade == 0) continue;
            for (var x = 0; x < Width; x++)
            {
                var distance = Math.Min(Math.Min(x, Width - 1 - x), edge);
                var amount = Math.Max(0, 24 - distance / 7);
                if (amount == 0) continue;
                var index = (y * Width + x) * 3;
                pixels[index] = (byte)Math.Max(0, pixels[index] - amount);
                pixels[index + 1] = (byte)Math.Max(0, pixels[index + 1] - amount);
                pixels[index + 2] = (byte)Math.Max(0, pixels[index + 2] - amount);
            }
        }
    }

    private static bool[] CreateMask(int seed)
    {
        var mask = new bool[PieceSize * PieceSize];
        var centers = new (int X, int Y, bool Protrude)[]
        {
            (PieceSize / 2, TabRadius, (seed & 1) == 0),
            (PieceSize - TabRadius - 1, PieceSize / 2, (seed & 2) != 0),
            (PieceSize / 2, PieceSize - TabRadius - 1, (seed & 4) == 0),
            (TabRadius, PieceSize / 2, (seed & 8) != 0)
        };
        var radius = TabRadius;
        for (var y = 0; y < PieceSize; y++)
        for (var x = 0; x < PieceSize; x++)
        {
            var inside = x >= radius && x < PieceSize - radius && y >= radius && y < PieceSize - radius;
            foreach (var center in centers)
            {
                var dx = x - center.X;
                var dy = y - center.Y;
                var circle = dx * dx + dy * dy <= radius * radius;
                if (center.Protrude && circle) inside = true;
                if (!center.Protrude && circle) inside = false;
            }
            mask[y * PieceSize + x] = inside;
        }
        return mask;
    }

    private static void ShadeGap(byte[] pixels, int left, int top, bool[] mask)
    {
        for (var y = 0; y < PieceSize; y++)
        for (var x = 0; x < PieceSize; x++)
        {
            if (!mask[y * PieceSize + x]) continue;
            var offset = ((top + y) * Width + left + x) * 3;
            pixels[offset] = (byte)(pixels[offset] * .34);
            pixels[offset + 1] = (byte)(pixels[offset + 1] * .34);
            pixels[offset + 2] = (byte)(pixels[offset + 2] * .34);
        }
        DrawMaskEdge(pixels, left, top, mask, (242, 237, 217), 2);
        DrawMaskEdge(pixels, left, top, mask, (40, 38, 44), 1);
    }

    private static void DrawMaskEdge(byte[] pixels, int left, int top, bool[] mask, (int R, int G, int B) color, int width)
    {
        for (var y = 0; y < PieceSize; y++)
        for (var x = 0; x < PieceSize; x++)
        {
            if (!mask[y * PieceSize + x]) continue;
            var edge = x < width || y < width || x >= PieceSize - width || y >= PieceSize - width
                || !mask[y * PieceSize + Math.Max(0, x - 1)] || !mask[y * PieceSize + Math.Min(PieceSize - 1, x + 1)]
                || !mask[Math.Max(0, y - 1) * PieceSize + x] || !mask[Math.Min(PieceSize - 1, y + 1) * PieceSize + x];
            if (edge) SetPixel(pixels, left + x, top + y, color.R, color.G, color.B);
        }
    }

    private static void FillPolygon(byte[] pixels, (int X, int Y)[] points, (int R, int G, int B) color)
    {
        var minY = Math.Max(0, points.Min(point => point.Y));
        var maxY = Math.Min(Height - 1, points.Max(point => point.Y));
        Span<int> intersections = stackalloc int[32];
        for (var y = minY; y <= maxY; y++)
        {
            var count = 0;
            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Length];
                if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                {
                    intersections[count++] = a.X + (int)((long)(y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            }
            intersections[..count].Sort();
            for (var i = 0; i + 1 < count; i += 2)
                for (var x = Math.Max(0, intersections[i]); x <= Math.Min(Width - 1, intersections[i + 1]); x++) SetPixel(pixels, x, y, color.R, color.G, color.B);
        }
    }

    private static void FillCircle(byte[] pixels, int cx, int cy, int radius, (int R, int G, int B) color)
    {
        for (var y = Math.Max(0, cy - radius); y <= Math.Min(Height - 1, cy + radius); y++)
        for (var x = Math.Max(0, cx - radius); x <= Math.Min(Width - 1, cx + radius); x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius) SetPixel(pixels, x, y, color.R, color.G, color.B);
    }

    private static void FillRect(byte[] pixels, int x, int y, int width, int height, (int R, int G, int B) color)
    {
        for (var row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
        for (var column = Math.Max(0, x); column < Math.Min(Width, x + width); column++) SetPixel(pixels, column, row, color.R, color.G, color.B);
    }

    private static void DrawLine(byte[] pixels, int x1, int y1, int x2, int y2, (int R, int G, int B) color, int thickness)
    {
        var steps = Math.Max(Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        for (var i = 0; i <= steps; i++)
        {
            var x = x1 + (x2 - x1) * i / Math.Max(1, steps);
            var y = y1 + (y2 - y1) * i / Math.Max(1, steps);
            FillCircle(pixels, x, y, Math.Max(0, thickness / 2), color);
        }
    }

    private static void SetPixel(byte[] pixels, int x, int y, int r, int g, int b)
    {
        if ((uint)x >= Width || (uint)y >= Height) return;
        var offset = (y * Width + x) * 3;
        pixels[offset] = (byte)Math.Clamp(r, 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(g, 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(b, 0, 255);
    }

    private static int Lerp(int start, int end, double amount) => (int)Math.Round(start + (end - start) * amount);

    private static (int, int, int) Shift((int, int, int) color, int red, int green, int blue) =>
        (Math.Clamp(color.Item1 + red, 0, 255), Math.Clamp(color.Item2 + green, 0, 255), Math.Clamp(color.Item3 + blue, 0, 255));

    private static string ToDataUrl(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private static byte[] EncodePng(int width, int height, int channels, byte[] pixels)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;
        header[9] = channels == 4 ? (byte)6 : (byte)2;
        WriteChunk(output, "IHDR"u8, header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowLength = width * channels;
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels, y * rowLength, rowLength);
            }
        }
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        WriteBigEndian(crc, 0, ComputeCrc32(crcInput));
        output.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xffffffffu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static void WriteBigEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
