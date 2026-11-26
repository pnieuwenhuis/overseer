using System.Buffers.Binary;

namespace Overseer.Implementation;

/// <summary>
/// The kinds of image a screenshot may be: PNG, JPEG, GIF and WebP. A file counts as one only when its structure is whole, from
/// the header that names the kind to what ends it, so that one broken or cut short isn't kept, which a browser would show as a
/// broken image. The pixels aren't decoded. Bytes after the end of an image are let pass, as browsers do.
/// </summary>
internal static class ScreenshotImageFormats
{
    /// <summary>The media type of the image the bytes hold whole; <c>null</c> when they hold none a screenshot may be.</summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> content) =>
        IsPng(content) ? "image/png"
        : IsJpeg(content) ? "image/jpeg"
        : IsGif(content) ? "image/gif"
        : IsWebP(content) ? "image/webp"
        : null;

    /// <summary>
    /// A signature, then chunks, each a length, a type, that many bytes and a checksum: first an <c>IHDR</c> that gives the image
    /// a size, then among them image data (<c>IDAT</c>), and <c>IEND</c> to end them.
    /// </summary>
    private static bool IsPng(ReadOnlySpan<byte> content)
    {
        if (!content.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return false;
        }
        // The bytes of a chunk besides its data: its length, type and checksum.
        const int around = 12;
        var first = true;
        var hasData = false;
        for (var rest = content[8..]; rest.Length >= around;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(rest);
            if (length > rest.Length - around)
            {
                return false;
            }
            var type = rest.Slice(4, 4);
            var data = rest.Slice(8, (int)length);
            if (first && !(type.SequenceEqual("IHDR"u8) && data.Length == 13
                && BinaryPrimitives.ReadUInt32BigEndian(data) > 0 && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) > 0))
            {
                return false;
            }
            if (type.SequenceEqual("IEND"u8))
            {
                return hasData;
            }
            first = false;
            hasData |= type.SequenceEqual("IDAT"u8) && data.Length > 0;
            rest = rest[(around + (int)length)..];
        }
        return false;
    }

    /// <summary>
    /// The start-of-image marker, then segments, each a marker and mostly a length: a frame header that gives the image a size, at
    /// least one scan, whose coded data runs up to the next marker, and the end-of-image marker.
    /// </summary>
    private static bool IsJpeg(ReadOnlySpan<byte> content)
    {
        if (content is not [0xFF, 0xD8, ..])
        {
            return false;
        }
        var hasFrame = false;
        var hasScan = false;
        var at = 2;
        while (at + 1 < content.Length && content[at] == 0xFF)
        {
            var marker = content[at + 1];
            if (marker == 0xFF)
            {
                // A fill byte ahead of the marker.
                at++;
                continue;
            }
            at += 2;
            if (marker == 0xD9)
            {
                return hasScan;
            }
            // These stand alone, without a length.
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                continue;
            }
            if (at + 2 > content.Length)
            {
                return false;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(content[at..]);
            if (length < 2 || length > content.Length - at)
            {
                return false;
            }
            // A start of frame, of any coding. The three others in this range define tables, or are reserved.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // After the length: the precision, then the height and the width.
                if (length < 8 || BinaryPrimitives.ReadUInt16BigEndian(content[(at + 3)..]) == 0
                    || BinaryPrimitives.ReadUInt16BigEndian(content[(at + 5)..]) == 0)
                {
                    return false;
                }
                hasFrame = true;
            }
            at += length;
            // A start of scan.
            if (marker == 0xDA)
            {
                if (!hasFrame)
                {
                    return false;
                }
                hasScan = true;
                // In the coded data 0xFF comes only ahead of a zero, or as a restart marker; anything else is the next marker.
                while (at + 1 < content.Length && !(content[at] == 0xFF && content[at + 1] is not (0x00 or >= 0xD0 and <= 0xD7)))
                {
                    at++;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// A header and a screen descriptor, then blocks: extensions and at least one image, each with its data in sub-blocks, and the
    /// trailer that ends them.
    /// </summary>
    private static bool IsGif(ReadOnlySpan<byte> content)
    {
        if (content.Length < 13 || !(content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8)))
        {
            return false;
        }
        var at = 13 + ColourTableLength(content[10]);
        var hasImage = false;
        while (at < content.Length)
        {
            switch (content[at])
            {
                case 0x3B:
                    return hasImage;
                case 0x21:
                    // Past the introducer and the extension's label, to its sub-blocks.
                    at += 2;
                    break;
                case 0x2C:
                    // An image's descriptor: where it goes, its width and height, and what colours it has.
                    if (at + 10 > content.Length || BinaryPrimitives.ReadUInt16LittleEndian(content[(at + 5)..]) == 0
                        || BinaryPrimitives.ReadUInt16LittleEndian(content[(at + 7)..]) == 0)
                    {
                        return false;
                    }
                    // Past its colours and the code size, to its coded data, of which there is at least one sub-block.
                    at += 10 + ColourTableLength(content[at + 9]) + 1;
                    if (at >= content.Length || content[at] == 0)
                    {
                        return false;
                    }
                    hasImage = true;
                    break;
                default:
                    return false;
            }
            // Each sub-block is its length and that many bytes; an empty one ends them.
            while (at < content.Length && content[at] != 0)
            {
                at += content[at] + 1;
            }
            at++;
        }
        return false;

        static int ColourTableLength(byte flags) => (flags & 0x80) == 0 ? 0 : 3 << ((flags & 0x07) + 1);
    }

    /// <summary>
    /// A RIFF file of the WebP kind that is as long as it says, whose chunks all fit in it, with an image among them: a lossy or
    /// lossless one, or the frames of an animation.
    /// </summary>
    private static bool IsWebP(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12 || !content.StartsWith("RIFF"u8) || !content[8..].StartsWith("WEBP"u8))
        {
            return false;
        }
        // Of what follows these first eight bytes.
        var length = BinaryPrimitives.ReadUInt32LittleEndian(content[4..]);
        if (length < 4 || length > content.Length - 8)
        {
            return false;
        }
        var hasImage = false;
        for (var rest = content[12..(8 + (int)length)]; rest.Length > 0;)
        {
            if (rest.Length < 8)
            {
                return false;
            }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(rest[4..]);
            if (size > rest.Length - 8)
            {
                return false;
            }
            var data = rest.Slice(8, (int)size);
            if (rest.StartsWith("VP8 "u8))
            {
                // A key frame's start code follows the three bytes of its tag.
                if (data.Length < 10 || !data[3..].StartsWith((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A]))
                {
                    return false;
                }
                hasImage = true;
            }
            else if (rest.StartsWith("VP8L"u8))
            {
                if (data.Length < 5 || data[0] != 0x2F)
                {
                    return false;
                }
                hasImage = true;
            }
            else if (rest.StartsWith("ANMF"u8))
            {
                // A frame of an animation: where it goes and for how long, in sixteen bytes, then its image.
                if (data.Length <= 16)
                {
                    return false;
                }
                hasImage = true;
            }
            // A chunk of an odd size is padded to an even one.
            rest = rest[Math.Min(rest.Length, 8 + (int)size + (int)(size & 1))..];
        }
        return hasImage;
    }
}
