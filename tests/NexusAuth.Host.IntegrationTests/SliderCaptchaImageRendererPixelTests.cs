using System.Buffers.Binary;
using System.IO.Compression;
using NexusAuth.Host.Authentication;
using Xunit;

namespace NexusAuth.Host.IntegrationTests;

public sealed class SliderCaptchaImageRendererPixelTests
{
    [Theory]
    [InlineData(60, 300)]
    [InlineData(240, 300)]
    public void Render_aligns_transparent_piece_with_darkened_background_gap(int targetOffset, int trackWidth)
    {
        var images = SliderCaptchaImageRenderer.Render(targetOffset, trackWidth);
        var background = DecodePng(images.BackgroundDataUrl);
        var piece = DecodePng(images.PieceDataUrl);

        Assert.Equal((images.Width, images.Height, 3), (background.Width, background.Height, background.Channels));
        Assert.Equal((images.PieceSize, images.PieceSize, 4), (piece.Width, piece.Height, piece.Channels));
        Assert.InRange(images.PieceTop, 0, images.Height - images.PieceSize);

        var alphaValues = Enumerable.Range(0, piece.Width * piece.Height)
            .Select(index => piece.Pixels[index * 4 + 3])
            .ToArray();
        Assert.Contains((byte)0, alphaValues);
        Assert.Contains((byte)255, alphaValues);
        AssertConnected(alphaValues, piece.Width, piece.Height);

        var expectedLeft = (int)Math.Round(targetOffset / (double)trackWidth * (images.Width - images.PieceSize));
        var centerX = images.PieceSize / 2;
        var centerY = images.PieceSize / 2;
        Assert.Equal((byte)255, piece.Get(centerX, centerY, 3));

        for (var channel = 0; channel < 3; channel++)
        {
            var pieceChannel = piece.Get(centerX, centerY, channel);
            var gapChannel = background.Get(expectedLeft + centerX, images.PieceTop + centerY, channel);
            Assert.Equal((byte)(pieceChannel * .34), gapChannel);
        }

    }

    private static void AssertConnected(byte[] alpha, int width, int height)
    {
        var start = Array.FindIndex(alpha, value => value == 255);
        Assert.True(start >= 0);
        var visited = new bool[alpha.Length];
        var queue = new Queue<int>();
        queue.Enqueue(start);
        visited[start] = true;
        var connected = 0;
        while (queue.TryDequeue(out var current))
        {
            connected++;
            var x = current % width;
            var y = current / width;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nextX = x + dx;
                var nextY = y + dy;
                if ((uint)nextX >= width || (uint)nextY >= height) continue;
                var next = nextY * width + nextX;
                if (visited[next] || alpha[next] != 255) continue;
                visited[next] = true;
                queue.Enqueue(next);
            }
        }
        Assert.Equal(alpha.Count(value => value == 255), connected);
    }

    private static DecodedPng DecodePng(string dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, dataUrl);
        var png = Convert.FromBase64String(dataUrl[prefix.Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);

        var offset = 8;
        var idat = new MemoryStream();
        int width = 0, height = 0, channels = 0;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            offset += 4;
            var type = System.Text.Encoding.ASCII.GetString(png, offset, 4);
            offset += 4;
            var data = png.AsSpan(offset, length);
            if (type == "IHDR")
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)));
                channels = data[9] switch { 2 => 3, 6 => 4, _ => throw new InvalidDataException("Unexpected PNG color type.") };
                Assert.Equal((byte)8, data[8]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            offset += length + 4;
            if (type == "IEND") break;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var decoded = raw.ToArray();
        var rowLength = width * channels;
        var pixels = new byte[height * rowLength];
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * (rowLength + 1);
            Assert.Equal((byte)0, decoded[rowOffset]);
            decoded.AsSpan(rowOffset + 1, rowLength).CopyTo(pixels.AsSpan(y * rowLength, rowLength));
        }
        return new DecodedPng(width, height, channels, pixels);
    }

    private sealed record DecodedPng(int Width, int Height, int Channels, byte[] Pixels)
    {
        public byte Get(int x, int y, int channel) => Pixels[(y * Width + x) * Channels + channel];
    }
}
