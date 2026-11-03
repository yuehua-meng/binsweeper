using System.Security.Principal;

namespace BinSweeper.Core.RecycleBin;

/// <summary>某一个卷上的当前用户回收站目录。</summary>
public sealed record RecycleBinVolume(string RootPath, string BinDirectory);

public static class RecycleBinLocator
{
    /// <summary>回收站位于 卷根\$Recycle.Bin\&lt;当前用户 SID&gt;。</summary>
    public static string? CurrentUserSid()
        => WindowsIdentity.GetCurrent().User?.Value;

    public static IReadOnlyList<RecycleBinVolume> LocateCurrentUser()
        => Locate(CurrentUserSid());

    public static IReadOnlyList<RecycleBinVolume> Locate(string? sid)
    {
        if (string.IsNullOrEmpty(sid))
        {
            return Array.Empty<RecycleBinVolume>();
        }

        var volumes = new List<RecycleBinVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var binDirectory = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(binDirectory))
            {
                volumes.Add(new RecycleBinVolume(drive.RootDirectory.FullName, binDirectory));
            }
        }

        return volumes;
    }
}
