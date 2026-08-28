namespace Weft.Core.Release;

/// <summary>Why an update cannot be written.</summary>
public sealed record SwapObstacle(string Reason, string? Remedy = null);

/// <summary>
/// Replaces the binary that is currently running.
/// </summary>
/// <remarks>
/// <para>The two systems disagree about whether that is even allowed, and the
/// difference is the whole of this file.</para>
///
/// <para>On Unix a rename replaces the path while the running process keeps the
/// inode it started from, so the swap is one atomic call and the process carries
/// on unbothered. On Windows the file is locked for writing and deletion, but
/// <em>renaming</em> it is permitted: the running binary is moved aside, the new
/// one takes its name, and the old one is removed the next time weft starts,
/// when nothing holds it.</para>
/// </remarks>
public static class BinarySwap
{
    /// <summary>What a superseded binary is renamed to while it is still running.</summary>
    public const string OldSuffix = ".weft-old";

    /// <summary>Whether this process is a published binary that can replace itself.</summary>
    /// <remarks>
    /// Running through 'dotnet run' means the executable is the dotnet host, and
    /// replacing that would be replacing the user's SDK. Refusing with a reason
    /// beats overwriting something nobody asked about.
    /// </remarks>
    public static SwapObstacle? CanReplace(string target)
    {
        if (!File.Exists(target))
            return new SwapObstacle($"'{target}' is not there", "reinstall weft");

        var name = Path.GetFileNameWithoutExtension(target);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new SwapObstacle(
                "this is running through the .NET host rather than as an installed binary",
                "build and install it, or use the installer");

        // Probed by writing rather than by reading permissions. The permission
        // bits are only part of the answer: an immutable flag, a read-only mount
        // or a directory that denies writes all produce a file that looks
        // writable and is not.
        var probe = target + ".weft-write-probe";
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return new SwapObstacle(
                $"cannot write next to '{target}' ({e.GetType().Name})",
                OperatingSystem.IsWindows()
                    ? "run the terminal as administrator, or reinstall somewhere you own"
                    : $"try: sudo weft up   (or reinstall with WEFT_BIN_DIR set to somewhere you own)");
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch (IOException) { }
        }
    }

    /// <summary>Puts <paramref name="staged"/> in place of <paramref name="target"/>.</summary>
    public static void Replace(string target, string staged)
    {
        if (!OperatingSystem.IsWindows())
        {
            // The executable bit is set here rather than assumed from the
            // download: a file fetched over HTTP arrives 0644 whatever it was on
            // the machine that built it.
            File.SetUnixFileMode(staged,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            File.Move(staged, target, overwrite: true);
            return;
        }

        var aside = target + OldSuffix;

        // A leftover from a previous update would make the rename below fail, and
        // the failure would look like the new problem rather than the old one.
        try { if (File.Exists(aside)) File.Delete(aside); } catch (IOException) { }

        File.Move(target, aside, overwrite: true);

        try
        {
            File.Move(staged, target, overwrite: false);
        }
        catch
        {
            // Put the running binary back. Leaving the path empty because an
            // update half-succeeded would uninstall weft.
            try { File.Move(aside, target, overwrite: false); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Removes a binary left aside by an earlier update on Windows.
    /// </summary>
    /// <returns>True when something was cleaned up.</returns>
    /// <remarks>
    /// Called at start-up. The file cannot be deleted while it is running, so the
    /// deletion has to happen in a later process, and a failure here is not worth
    /// mentioning: it means the previous binary is somehow still in use, and it
    /// will be tried again next time.
    /// </remarks>
    public static bool CleanupLeftovers(string target)
    {
        var aside = target + OldSuffix;
        if (!File.Exists(aside)) return false;

        try { File.Delete(aside); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
