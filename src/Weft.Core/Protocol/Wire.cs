using System.Text.Json.Serialization;

namespace Weft.Core.Protocol;

/// <summary>Versions the client and server negotiate on.</summary>
public static class WeftVersion
{
    /// <summary>
    /// The wire protocol. Bumped only when the shape of a request or response
    /// changes, never when the binary changes.
    /// </summary>
    /// <remarks>
    /// Kept apart from the build version on purpose. An older build that still
    /// speaks the current protocol has no reason to be refused, and conflating
    /// the two turns every release into a forced upgrade across every machine.
    /// </remarks>
    public const int Protocol = 1;

    /// <summary>
    /// This build.
    /// </summary>
    /// <remarks>
    /// Must match VersionPrefix in Directory.Build.props, which is what
    /// 'weft --version' prints. A test enforces it. They drifted apart once and
    /// the binary reported 1.0.0 while the server gated writes on 0.2.0: the
    /// number a person could see was not the number being judged.
    /// </remarks>
    public const string Build = "0.4.0";

    /// <summary>Header carrying the oldest build the server will accept writes from.</summary>
    public const string MinClientHeader = "Weft-Min-Client";

    /// <summary>Header carrying the protocol the server speaks.</summary>
    public const string ProtocolHeader = "Weft-Protocol";
}

/// <summary>What a server says about itself. Reachable without a token.</summary>
public sealed record ServerInfo(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("minClient")] string MinClient,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("workspace")] string WorkspaceFingerprint);

/// <summary>Asks a server to accept a machine.</summary>
/// <param name="JoinSecret">Proves the operator meant to add this machine.</param>
/// <param name="WorkspaceFingerprint">
/// Non-secret fingerprint of the workspace key. Lets the server refuse a machine
/// holding a different key at enrolment, rather than letting it upload objects
/// nobody else can decrypt and discover the mistake months later.
/// </param>
public sealed record EnrolRequest(
    [property: JsonPropertyName("joinSecret")] string JoinSecret,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("workspace")] string WorkspaceFingerprint);

/// <summary>The credential a machine uses from then on. Shown once.</summary>
public sealed record EnrolResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("machineId")] string MachineId);

/// <summary>Asks which of these objects the server lacks.</summary>
/// <remarks>
/// The heart of an efficient push. Uploading everything and letting the server
/// discard duplicates would send the whole workspace every time; asking first
/// costs one round trip and a list of names.
/// </remarks>
public sealed record MissingRequest(
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids);

public sealed record MissingResponse(
    [property: JsonPropertyName("missing")] IReadOnlyList<string> Missing);

/// <summary>Where one machine stands.</summary>
/// <param name="RevokedUtc">
/// When its token was withdrawn, null while it is still allowed in. A revoked
/// machine keeps its row and its pointer: the objects it pushed are what someone
/// would be trying to recover, and dropping the pointer would hide them.
/// </param>
public sealed record HeadEntry(
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("snapshot")] string? Snapshot,
    [property: JsonPropertyName("updatedUtc")] DateTimeOffset? UpdatedUtc,
    [property: JsonPropertyName("lastSeenUtc")] DateTimeOffset LastSeenUtc,
    [property: JsonPropertyName("revokedUtc")] DateTimeOffset? RevokedUtc = null);

/// <summary>Withdraws one machine's token.</summary>
/// <remarks>
/// Authenticated by the join secret, never by a bearer token, and that choice is
/// the whole point. The machine being revoked is usually the one that was lost,
/// and it holds a token; it does not hold the join secret, which is never written
/// to disk by the client. Accepting a token here would let whoever took the
/// machine revoke everyone else first.
/// </remarks>
public sealed record RevokeRequest(
    [property: JsonPropertyName("joinSecret")] string JoinSecret);

public sealed record HeadsResponse(
    [property: JsonPropertyName("heads")] IReadOnlyList<HeadEntry> Heads);

/// <summary>Moves this machine's pointer. A machine can only ever move its own.</summary>
public sealed record SetHeadRequest(
    [property: JsonPropertyName("snapshot")] string Snapshot);

/// <summary>Anything that went wrong, in a shape the client can act on.</summary>
public sealed record ApiError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("code")] string Code);

[JsonSerializable(typeof(ServerInfo))]
[JsonSerializable(typeof(EnrolRequest))]
[JsonSerializable(typeof(EnrolResponse))]
[JsonSerializable(typeof(MissingRequest))]
[JsonSerializable(typeof(MissingResponse))]
[JsonSerializable(typeof(HeadsResponse))]
[JsonSerializable(typeof(HeadEntry))]
[JsonSerializable(typeof(SetHeadRequest))]
[JsonSerializable(typeof(RevokeRequest))]
[JsonSerializable(typeof(ApiError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class WireJson : JsonSerializerContext;
