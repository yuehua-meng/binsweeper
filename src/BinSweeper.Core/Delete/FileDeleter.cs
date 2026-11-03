namespace BinSweeper.Core.Delete;

/// <summary>删除文件/目录树，处理只读属性；遇到重解析点只删链接本身，不进入目标。</summary>
public static class FileDeleter
{
    public static void DeleteFileIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        ClearReadOnly(path);
        File.Delete(path);
    }

    public static void DeleteDirectoryTree(string root)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                var attributes = directory.Attributes;
                ClearReadOnly(directory.FullName);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(directory.FullName, recursive: false);
                }
                else
                {
                    DeleteDirectoryTree(directory.FullName);
                }
            }
            else
            {
                ClearReadOnly(entry.FullName);
                File.Delete(entry.FullName);
            }
        }

        ClearReadOnly(root);
        Directory.Delete(root, recursive: false);
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
