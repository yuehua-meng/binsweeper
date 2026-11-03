using System.Buffers.Binary;
using System.Text;

namespace BinSweeper.Tests;

internal static class TestData
{
    /// <summary>构造 Vista 及以后格式的 $I 元数据字节。</summary>
    public static byte[] BuildInfoFile(long originalSize, DateTime deletedAtUtc, string originalPath)
    {
        var bytes = new byte[28 + originalPath.Length * 2];

        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, 8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8, 8), originalSize);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16, 8), deletedAtUtc.ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), originalPath.Length);

        var pathBytes = Encoding.Unicode.GetBytes(originalPath);
        pathBytes.CopyTo(bytes.AsSpan(28));

        return bytes;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        FullPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "binsweeper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 测试临时目录清理失败不影响结论。
        }
    }
}
