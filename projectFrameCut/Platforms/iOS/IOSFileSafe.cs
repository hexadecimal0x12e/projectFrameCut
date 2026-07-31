using Foundation;

namespace projectFrameCut.Platforms.iOS;

/// <summary>
/// iOS sandbox-safe file operations.
/// Apple has progressively hardened the sandbox — POSIX stat/opendir/mkdir on paths
/// that traverse the app container root now return EPERM ("Operation not permitted").
/// System.IO.Directory/File internally use these POSIX syscalls and will throw
/// UnauthorizedAccessException.
///
/// NSFileManager is sandbox-aware and uses Apple's recommended access paths,
/// so we fall back to it when System.IO fails.
/// </summary>
internal static class IOSFileSafe
{
    /// <summary>Sandbox-safe directory creation. Returns true if directory exists after the call.</summary>
    public static bool CreateDirectory(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // NSFileManager handles iOS sandbox correctly.
            return NSFileManager.DefaultManager.CreateDirectory(
                NSUrl.FromFilename(path), true, null, out var error);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Sandbox-safe file-existence check.</summary>
    public static bool FileExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return System.IO.File.Exists(path);
        }
        catch
        {
            try { return NSFileManager.DefaultManager.FileExists(path); }
            catch { return false; }
        }
    }

    /// <summary>Sandbox-safe directory-existence check.</summary>
    public static bool DirectoryExists(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return System.IO.Directory.Exists(path);
        }
        catch
        {
            try
            {
                bool isDir = false;
                var exists = NSFileManager.DefaultManager.FileExists(path, ref isDir);
                return exists && isDir;
            }
            catch { return false; }
        }
    }

    /// <summary>Sandbox-safe ReadAllText. Returns null on failure.</summary>
    public static string? ReadAllText(string path)
    {
        try
        {
            return System.IO.File.ReadAllText(path);
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                var data = NSData.FromFile(path);
                return data is not null ? NSString.FromData(data, NSStringEncoding.UTF8) : null;
            }
            catch { return null; }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sandbox-safe WriteAllText.</summary>
    public static bool WriteAllText(string path, string content)
    {
        try
        {
            System.IO.File.WriteAllText(path, content);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                var data = NSData.FromString(content, NSStringEncoding.UTF8);
                return data.Save(path, true);
            }
            catch { return false; }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sandbox-safe directory enumeration. Returns empty array on failure.</summary>
    public static string[] GetDirectories(string path, string pattern = "*")
    {
        try
        {
            return System.IO.Directory.GetDirectories(path, pattern);
        }
        catch (UnauthorizedAccessException)
        {
            // iOS blocks opendir through container root.
            // NSFileManager has no direct equivalent for GetDirectories
            // that works on sandboxed paths, so return empty.
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Sandbox-safe file enumeration. Returns empty array on failure.</summary>
    public static string[] GetFiles(string path, string pattern = "*")
    {
        try
        {
            return System.IO.Directory.GetFiles(path, pattern);
        }
        catch
        {
            return [];
        }
    }
}
