using Weft.Core.Ignore;

namespace Weft.Core.Workspace;

/// <summary>The directory weft manages, and the rules that govern it.</summary>
public sealed class WeftRoot
{
    public const string MetaDir = ".weft";
    public const string IgnoreFile = ".weftignore";
    public const string NeverFile = ".weftnever";

    public required string Path { get; init; }
    public required IgnorePolicy Policy { get; init; }

    /// <summary>False when no '.weft' directory exists: rules fall back to the built-in defaults.</summary>
    public required bool IsInitialised { get; init; }

    public string MetaPath => System.IO.Path.Combine(Path, MetaDir);

    /// <summary>Where the object store lives.</summary>
    public string StorePath => System.IO.Path.Combine(MetaPath, "store");

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a '.weft' directory.
    /// </summary>
    /// <remarks>
    /// When none is found, the start directory is used with the built-in rules so
    /// that a read-only command still works. Anything that writes must check
    /// <see cref="IsInitialised"/> first: silently initialising a workspace
    /// because someone ran a command in the wrong directory would be a surprising
    /// and hard-to-undo side effect.
    /// </remarks>
    public static WeftRoot Discover(string startDir)
    {
        var dir = new DirectoryInfo(System.IO.Path.GetFullPath(startDir));

        for (var d = dir; d is not null; d = d.Parent)
        {
            if (!Directory.Exists(System.IO.Path.Combine(d.FullName, MetaDir))) continue;

            return new WeftRoot
            {
                Path = d.FullName,
                Policy = LoadPolicy(d.FullName),
                IsInitialised = true,
            };
        }

        return new WeftRoot
        {
            Path = dir.FullName,
            Policy = IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never),
            IsInitialised = false,
        };
    }

    private static IgnorePolicy LoadPolicy(string root)
    {
        var ignore = ReadOr(System.IO.Path.Combine(root, IgnoreFile), DefaultRules.Ignore);

        // The confidentiality set falls back to the built-in defaults when the
        // file is missing, never to an empty set. A deleted .weftnever must not
        // quietly turn into "nothing is confidential".
        var never = ReadOr(System.IO.Path.Combine(root, NeverFile), DefaultRules.Never);

        return IgnorePolicy.Parse(ignore, never);
    }

    private static string ReadOr(string path, string fallback)
        => File.Exists(path) ? File.ReadAllText(path) : fallback;
}
