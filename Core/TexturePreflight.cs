using System;
using System.IO;
using System.Text;

namespace WpeRemix.Core;

internal static class TexturePreflight
{
    internal static void Validate(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            if (input.Length > SafeFiles.MaximumFileBytes)
            {
                throw new InvalidDataException("TEX 超过 512 MB 限制");
            }
            using var reader = new BinaryReader(input, Encoding.UTF8, true);
            if (ReadMagic(reader) != "TEXV0005" || ReadMagic(reader) != "TEXI0001")
            {
                throw new InvalidDataException("未知 TEX 版本");
            }
            reader.ReadInt32();
            int flags = reader.ReadInt32();
            if ((flags & 4) != 0)
            {
                throw new InvalidDataException("动画 TEX 暂不转换；原 TEX 将保留在素材目录");
            }
            ValidateDimensions(reader.ReadInt32(), reader.ReadInt32());
            ValidateDimensions(reader.ReadInt32(), reader.ReadInt32());
            reader.ReadInt32();
            string container = ReadMagic(reader);
            int imageCount = reader.ReadInt32();
            if (imageCount is < 1 or > 512)
            {
                throw new InvalidDataException("TEX 图像帧数量无效或超过 512 帧");
            }
            int version;
            if (!container.StartsWith("TEXB", StringComparison.Ordinal) || !int.TryParse(container.AsSpan(4), out version) || version is < 1 or > 4)
            {
                throw new InvalidDataException("未知 TEXB 版本");
            }
            if (version >= 3)
            {
                reader.ReadInt32();
            }
            if (version == 4)
            {
                bool video = reader.ReadInt32() == 1;
                if (!video)
                {
                    version = 3;
                }
            }
            long decompressedTotal = 0;
            for (int image = 0; image < imageCount; image++)
            {
                int mipmaps = reader.ReadInt32();
                if (mipmaps is < 1 or > 32)
                {
                    throw new InvalidDataException("TEX mipmap 数量无效");
                }
                for (int mipmap = 0; mipmap < mipmaps; mipmap++)
                {
                    if (version == 4)
                    {
                        if (reader.ReadInt32() != 1 || reader.ReadInt32() != 2)
                        {
                            throw new InvalidDataException("TEX 视频头无效");
                        }
                        ReadMagic(reader, 16384);
                        if (reader.ReadInt32() != 1)
                        {
                            throw new InvalidDataException("TEX 视频头无效");
                        }
                    }
                    int width = reader.ReadInt32();
                    int height = reader.ReadInt32();
                    ValidateDimensions(width, height);
                    long decompressed = (long)width * height * 4;
                    if (version >= 2)
                    {
                        reader.ReadInt32();
                        int declared = reader.ReadInt32();
                        if (declared < 0 || declared > SafeFiles.MaximumFileBytes)
                        {
                            throw new InvalidDataException("TEX 解压长度无效或过大");
                        }
                        decompressed = Math.Max(decompressed, declared);
                    }
                    decompressedTotal = checked(decompressedTotal + decompressed);
                    if (decompressedTotal > 1024L * 1024 * 1024)
                    {
                        throw new InvalidDataException("TEX 预计展开超过 1 GB");
                    }
                    int length = reader.ReadInt32();
                    if (length < 0 || length > SafeFiles.MaximumFileBytes || length > input.Length - input.Position)
                    {
                        throw new InvalidDataException("TEX 图像数据长度无效或被截断");
                    }
                    input.Position += length;
                }
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("TEX 文件已截断", exception);
        }
    }

    private static string ReadMagic(BinaryReader reader, int maximum = 16)
    {
        using var bytes = new MemoryStream();
        for (int index = 0; index < maximum; index++)
        {
            byte character = reader.ReadByte();
            if (character == 0)
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.WriteByte(character);
        }
        throw new InvalidDataException("TEX 字符串超出限制");
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > 32768 || height is < 1 or > 32768 || (long)width * height > 100000000)
        {
            throw new InvalidDataException("TEX 图像尺寸无效或超过 1 亿像素");
        }
    }
}
