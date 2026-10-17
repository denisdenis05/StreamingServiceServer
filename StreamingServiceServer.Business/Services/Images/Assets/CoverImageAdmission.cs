using System.Buffers.Binary;

namespace StreamingServiceServer.Business.Services.Images.Assets;

public static class CoverImageAdmission
{
    private const int MaximumHeaderBytes = 262144;
    public static async Task ValidateAsync(string path, long maximumDecodedPixels, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        var header = new byte[(int)Math.Min(input.Length, MaximumHeaderBytes)];
        await input.ReadExactlyAsync(header, cancellationToken);
        var dimensions = ReadDimensions(header);
        if (dimensions.Width <= 0 || dimensions.Height <= 0 || (long)dimensions.Width * dimensions.Height > maximumDecodedPixels)
            throw new IOException("Cover image dimensions exceed the configured decode limit or cannot be read safely.");
    }

    public static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 24 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ((int)BinaryPrimitives.ReadUInt32BigEndian(header[16..20]), (int)BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
        if (header.Length >= 30 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            if (header[12..16].SequenceEqual("VP8X"u8)) return (Unsigned24(header[24..27]) + 1, Unsigned24(header[27..30]) + 1);
            if (header[12..16].SequenceEqual("VP8L"u8) && header[20] == 47)
            {
                var packed = BinaryPrimitives.ReadUInt32LittleEndian(header[21..25]);
                return ((int)(packed & 16383) + 1, (int)((packed >> 14) & 16383) + 1);
            }
            if (header[12..16].SequenceEqual("VP8 "u8) && header[23..26].SequenceEqual(new byte[] { 157, 1, 42 }))
                return (BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]) & 16383,
                    BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]) & 16383);
        }
        if (header.Length >= 4 && header[0] == 255 && header[1] == 216)
        {
            var offset = 2;
            while (offset + 4 <= header.Length)
            {
                if (header[offset] != 255) break;
                while (offset < header.Length && header[offset] == 255) offset++;
                if (offset >= header.Length) break;
                var marker = header[offset++];
                if (marker is 216 or 1 || marker is >= 208 and <= 215) continue;
                if (marker is 217 or 218 || offset + 2 > header.Length) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(header[offset..]);
                if (length < 2 || offset + length > header.Length) break;
                if (marker is >= 192 and <= 207 && marker is not (196 or 200 or 204) && length >= 7)
                    return (BinaryPrimitives.ReadUInt16BigEndian(header[(offset + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(header[(offset + 3)..]));
                offset += length;
            }
        }
        return default;
    }
    private static int Unsigned24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
