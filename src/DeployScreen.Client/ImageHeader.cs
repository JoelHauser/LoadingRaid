using System.IO;

namespace DeployScreen.Client
{
    /// <summary>
    /// Reads an image's pixel size from its header, without decoding it.
    ///
    /// Choosing between sizes of the same picture means knowing each file's size, and decoding
    /// every one to find out would load a 4K picture only to throw it away. A PNG states its size
    /// at a fixed offset; a JPEG states it in its frame header, which can sit after metadata of
    /// any length, so the JPEG reader walks the segments rather than reading a fixed prefix.
    /// </summary>
    internal static class ImageHeader
    {
        internal static bool TryReadSize(string path, out int width, out int height)
        {
            width = 0;
            height = 0;

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[24];
                    var read = stream.Read(head, 0, head.Length);

                    // PNG: signature, then IHDR's width and height as big-endian at 16 and 20.
                    if (read >= 24 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
                    {
                        width = BigEndian(head, 16);
                        height = BigEndian(head, 20);
                        return width > 0 && height > 0;
                    }

                    // JPEG: starts FF D8.
                    if (read >= 2 && head[0] == 0xFF && head[1] == 0xD8)
                    {
                        stream.Position = 2;
                        return ReadJpeg(stream, out width, out height);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (System.UnauthorizedAccessException)
            {
            }

            return false;
        }

        private static int BigEndian(byte[] bytes, int at)
        {
            return (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
        }

        private static bool ReadJpeg(Stream stream, out int width, out int height)
        {
            width = 0;
            height = 0;

            while (stream.Position < stream.Length)
            {
                if (stream.ReadByte() != 0xFF) return false;

                // Any number of FF bytes may pad before a marker.
                int marker;
                do { marker = stream.ReadByte(); } while (marker == 0xFF);

                if (marker < 0) return false;

                // Markers with no length: TEM and the restart markers.
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;

                // End of image, or image data starting, before any frame header.
                if (marker == 0xD9 || marker == 0xDA) return false;

                var high = stream.ReadByte();
                var low = stream.ReadByte();
                if (high < 0 || low < 0) return false;

                var length = (high << 8) | low;
                if (length < 2) return false;

                // SOF0-SOF15, less DHT (C4), JPG (C8) and DAC (CC), which share the range.
                var isFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

                if (isFrame)
                {
                    stream.ReadByte(); // sample precision

                    var h1 = stream.ReadByte();
                    var h2 = stream.ReadByte();
                    var w1 = stream.ReadByte();
                    var w2 = stream.ReadByte();
                    if ((h1 | h2 | w1 | w2) < 0) return false;

                    height = (h1 << 8) | h2;
                    width = (w1 << 8) | w2;
                    return width > 0 && height > 0;
                }

                stream.Position += length - 2;
            }

            return false;
        }
    }
}
