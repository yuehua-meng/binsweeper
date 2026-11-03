using System.Buffers.Binary;
using System.Text;

namespace BinSweeper.Core.RecycleBin;

/// <summary>
/// 解析 Vista 及以后版本的 $I 元数据文件：
/// 0..7 版本号 | 8..15 原始大小 | 16..23 删除时间(FILETIME) | 24..27 路径字符数 | 28.. 原始路径(UTF-16LE)
/// </summary>
public static class RecycleBinInfoParser
{
    private const int HeaderSize = 28;
    private const int Version2 = 2;

    public static RecycleBinInfo? TryParse(string metaPath, out string? error)
    {
        error = null;

        try
        {
            using var stream = new FileStream(
                metaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < HeaderSize)
            {
                error = "文件长度不足，不是有效的 $I 文件";
                return null;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);

            var version = BinaryPrimitives.ReadInt64LittleEndian(header[..8]);
            if (version != Version2)
            {
                error = version == 1
                    ? "不支持 $I 版本 1（Windows XP 及更早系统）"
                    : $"未知的 $I 版本 {version}";
                return null;
            }

            var originalSize = BinaryPrimitives.ReadInt64LittleEndian(header[8..16]);
            var deletedAt = TryReadFileTime(BinaryPrimitives.ReadInt64LittleEndian(header[16..24]));

            var pathCharCount = BinaryPrimitives.ReadInt32LittleEndian(header[24..28]);
            if (pathCharCount <= 0 || pathCharCount > 32767)
            {
                error = $"原始路径长度异常：{pathCharCount}";
                return null;
            }

            var pathBytes = pathCharCount * 2L;
            if (stream.Length < HeaderSize + pathBytes)
            {
                error = "原始路径数据被截断";
                return null;
            }

            var buffer = new byte[pathCharCount * 2];
            stream.ReadExactly(buffer);

            var originalPath = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            if (originalPath.Length == 0)
            {
                error = "原始路径为空";
                return null;
            }

            return new RecycleBinInfo(version, Math.Max(0, originalSize), deletedAt, originalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static DateTimeOffset? TryReadFileTime(long value)
    {
        if (value <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(value), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
