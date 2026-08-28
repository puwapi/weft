using System.Text.Json;
using System.Text.Json.Serialization;
using Weft.Core.Crypto;

namespace Weft.Core.Workspace;

/// <summary>Where a machine reaches its server, and with what credential.</summary>
public sealed record RemoteConfig(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("addedUtc")] DateTimeOffset AddedUtc);

[JsonSerializable(typeof(RemoteConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
public sealed partial class RemoteJson : JsonSerializerContext;

/// <summary>
/// Reads and writes the two secrets a workspace holds on disk.
/// </summary>
/// <remarks>
/// Both are written with owner-only permissions. On a shared machine the
/// workspace key is the whole of the encryption: a world-readable key file makes
/// the client-side encryption decorative, and nothing would ever report it.
/// </remarks>
public static class LocalSecrets
{
    public const string KeyFile = "key";
    public const string RemoteFile = "remote.json";

    public static string KeyPath(WeftRoot root) => Path.Combine(root.MetaPath, KeyFile);
    public static string RemotePath(WeftRoot root) => Path.Combine(root.MetaPath, RemoteFile);

    public static WorkspaceKey? TryLoadKey(WeftRoot root)
    {
        var p = KeyPath(root);
        if (!File.Exists(p)) return null;

        try { return WorkspaceKey.Parse(File.ReadAllText(p)); }
        catch (FormatException e)
        {
            // Never silently regenerate. A new key makes every object already on
            // the server undecryptable, and the symptom would be "authentication
            // failed" on content that is in fact intact.
            throw new InvalidOperationException(
                $"The workspace key at '{p}' is unreadable ({e.Message}). Restore it from another " +
                "machine rather than generating a new one: a new key orphans everything already stored.");
        }
    }

    public static void SaveKey(WeftRoot root, WorkspaceKey key)
    {
        Directory.CreateDirectory(root.MetaPath);
        var p = KeyPath(root);
        File.WriteAllText(p, key.ToDisplayString() + "\n");
        RestrictToOwner(p);
    }

    public static RemoteConfig? TryLoadRemote(WeftRoot root)
    {
        var p = RemotePath(root);
        if (!File.Exists(p)) return null;

        try { return JsonSerializer.Deserialize(File.ReadAllText(p), RemoteJson.Default.RemoteConfig); }
        catch (JsonException) { return null; }
    }

    public static void SaveRemote(WeftRoot root, RemoteConfig config)
    {
        Directory.CreateDirectory(root.MetaPath);
        var p = RemotePath(root);
        File.WriteAllText(p, JsonSerializer.Serialize(config, RemoteJson.Default.RemoteConfig));
        RestrictToOwner(p);
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;   // ACL inherited from the directory
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
