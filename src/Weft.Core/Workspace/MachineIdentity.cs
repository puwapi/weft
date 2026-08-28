using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weft.Core.Workspace;

/// <summary>Who this machine is, from the remote's point of view.</summary>
/// <param name="Id">
/// Stable for the life of the installation. A UUIDv7, so it sorts by creation
/// time, which makes "which machine joined first" answerable without a separate
/// field.
/// </param>
/// <param name="Name">Human label. Freely renameable; the id never moves.</param>
/// <param name="Platform">OS, for display only.</param>
/// <param name="CreatedUtc">When this identity was minted.</param>
public sealed record MachineIdentity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc)
{
    /// <summary>
    /// Mints a new identity.
    /// </summary>
    /// <remarks>
    /// The id is random, never derived from hostname or MAC address. A hostname
    /// changes, two machines can share one, and cloned virtual machines share
    /// both. Deriving identity from either produces two machines the remote
    /// believes are one, which is the hardest class of sync bug to diagnose
    /// because every symptom points somewhere else.
    ///
    /// The hostname is used as the default <em>name</em>, which is a label and
    /// carries no meaning.
    /// </remarks>
    public static MachineIdentity Mint(string? name = null) => new(
        Guid.CreateVersion7().ToString("n"),
        string.IsNullOrWhiteSpace(name) ? SafeHostName() : name.Trim(),
        Describe(),
        DateTimeOffset.UtcNow);

    /// <summary>
    /// A platform string a person can read.
    /// </summary>
    /// <remarks>
    /// Environment.OSVersion.Platform returns "Unix" for macOS and for Linux
    /// alike, so a machines table showing it tells you nothing about which is
    /// which, which is exactly what the column is for.
    /// </remarks>
    private static string Describe()
    {
        var os = OperatingSystem.IsMacOS() ? "macos"
               : OperatingSystem.IsWindows() ? "windows"
               : OperatingSystem.IsLinux() ? "linux"
               : "unknown";

        return $"{os}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }

    private static string SafeHostName()
    {
        try
        {
            var h = Environment.MachineName;
            return string.IsNullOrWhiteSpace(h) ? "unnamed" : h;
        }
        catch (InvalidOperationException) { return "unnamed"; }
    }
}

[JsonSerializable(typeof(MachineIdentity))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public sealed partial class MachineJson : JsonSerializerContext;

/// <summary>Reads and writes the machine identity file.</summary>
/// <remarks>
/// The identity lives under the user's home directory, not in the workspace.
/// One machine has one identity however many workspaces it holds, and a
/// workspace-local identity would be copied along with the workspace, producing
/// two machines claiming the same id.
/// </remarks>
public static class MachineStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weft", "machine.json");

    public static MachineIdentity? TryLoad(string? path = null)
    {
        var p = path ?? DefaultPath;
        if (!File.Exists(p)) return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(p), MachineJson.Default.MachineIdentity);
        }
        catch (JsonException)
        {
            // A corrupt identity file must not be silently replaced: minting a
            // new id would make the remote treat this machine as a stranger and
            // orphan everything it had already pushed.
            throw new InvalidOperationException(
                $"The machine identity at '{p}' is unreadable. Repair or remove it deliberately: " +
                "replacing it silently would give this machine a new identity and orphan its history.");
        }
    }

    public static MachineIdentity LoadOrMint(string? name = null, string? path = null)
    {
        var p = path ?? DefaultPath;
        var existing = TryLoad(p);
        if (existing is not null) return existing;

        var minted = MachineIdentity.Mint(name);
        Save(minted, p);
        return minted;
    }

    public static void Save(MachineIdentity id, string? path = null)
    {
        var p = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, JsonSerializer.Serialize(id, MachineJson.Default.MachineIdentity));
    }
}
