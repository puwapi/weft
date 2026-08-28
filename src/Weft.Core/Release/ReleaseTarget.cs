using System.Runtime.InteropServices;

namespace Weft.Core.Release;

/// <summary>Which published artefact this machine should be running.</summary>
/// <remarks>
/// The names have to match what the release workflow uploads, exactly. They are
/// derived here rather than guessed at the call site so that a rename breaks one
/// test instead of leaving 'weft up' downloading a 404 on one platform only.
/// </remarks>
public static class ReleaseTarget
{
    /// <summary>Asset name for a given system and architecture.</summary>
    public static string? AssetName(string os, string architecture)
    {
        var suffix = os switch
        {
            "macos" => "macos",
            "linux" => "linux",
            "windows" => "windows",
            _ => null,
        };
        if (suffix is null) return null;

        var arch = architecture switch
        {
            "x64" or "x86_64" or "amd64" => "x64",
            "arm64" or "aarch64" => "arm64",
            _ => null,
        };
        if (arch is null) return null;

        return $"weft-{suffix}-{arch}" + (suffix == "windows" ? ".exe" : "");
    }

    /// <summary>Asset name for the machine this is running on.</summary>
    public static string? Current()
    {
        var os = OperatingSystem.IsMacOS() ? "macos"
               : OperatingSystem.IsLinux() ? "linux"
               : OperatingSystem.IsWindows() ? "windows"
               : null;

        return os is null
            ? null
            : AssetName(os, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}
