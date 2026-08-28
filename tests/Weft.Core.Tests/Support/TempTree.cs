namespace Weft.Core.Tests.Support;

/// <summary>A temporary directory that really does go away.</summary>
internal static class TempTree
{
    public static string Create(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():n}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Removes a tree, including files something else marked read-only.
    /// </summary>
    /// <remarks>
    /// git marks its loose objects read-only, and on Windows a recursive delete
    /// respects that and throws UnauthorizedAccessException. On Unix the parent
    /// directory's write bit is what decides, so the same call succeeds and the
    /// difference is invisible until a test runs on the other platform. Any test
    /// that puts a real repository in a temp directory hits this.
    /// </remarks>
    public static void Remove(string path)
    {
        if (!Directory.Exists(path)) return;

        try { ClearReadOnly(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        try { Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void ClearReadOnly(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
