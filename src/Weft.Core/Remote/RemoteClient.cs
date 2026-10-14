using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Weft.Core.Crypto;
using Weft.Core.Protocol;

namespace Weft.Core.Remote;

/// <summary>The server said no, and said why.</summary>
public sealed class RemoteException(HttpStatusCode status, string code, string message)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>Machine-readable reason, so a caller can act rather than match on prose.</summary>
    public string Code { get; } = code;
}

/// <summary>Talks to a weft server.</summary>
public sealed class RemoteClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public RemoteClient(string baseUrl, string? token, HttpMessageHandler? handler = null)
    {
        _base = baseUrl.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);

        // Sent on every request so the server can refuse a write from a build it
        // no longer accepts, and say so in terms the client can report.
        _http.DefaultRequestHeaders.Add("Weft-Client", WeftVersion.Build);
        _http.DefaultRequestHeaders.Add("User-Agent", $"weft/{WeftVersion.Build}");

        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
    }

    public void Dispose() => _http.Dispose();

    public async Task<ServerInfo> InfoAsync(CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"{_base}/v1/info", ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
        return (await r.Content.ReadFromJsonAsync(WireJson.Default.ServerInfo, ct).ConfigureAwait(false))!;
    }

    public async Task<EnrolResponse> EnrolAsync(EnrolRequest request, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync($"{_base}/v1/enrol", request,
            WireJson.Default.EnrolRequest, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
        return (await r.Content.ReadFromJsonAsync(WireJson.Default.EnrolResponse, ct).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<HeadEntry>> HeadsAsync(CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"{_base}/v1/heads", ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
        return (await r.Content.ReadFromJsonAsync(WireJson.Default.HeadsResponse, ct).ConfigureAwait(false))!.Heads;
    }

    /// <param name="session">The push session that named this head's objects, or null without one.</param>
    public async Task SetHeadAsync(RemoteId snapshot, string? session = null, CancellationToken ct = default)
    {
        using var r = await _http.PutAsJsonAsync($"{_base}/v1/head",
            new SetHeadRequest(snapshot.ToString(), session), WireJson.Default.SetHeadRequest, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a push session, or returns null when the server predates them.
    /// </summary>
    /// <remarks>
    /// A server without sessions answers 404 on a route it does not know, and the
    /// push then goes the old way. Refusing instead would make upgrading the
    /// clients depend on upgrading the server first, for a feature only the
    /// server benefits from.
    /// </remarks>
    public async Task<string?> BeginPushAsync(CancellationToken ct = default)
    {
        using var r = await _http.PostAsync($"{_base}/v1/push", null, ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;

        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
        return (await r.Content.ReadFromJsonAsync(WireJson.Default.PushSessionResponse, ct).ConfigureAwait(false))!.Session;
    }

    /// <summary>
    /// Tells a push session what the head needs, and learns which of it the server lacks.
    /// </summary>
    /// <remarks>Batched like <see cref="MissingAsync"/>, for the same reasons.</remarks>
    public async Task<IReadOnlyList<RemoteId>> SessionMissingAsync(
        string session, IReadOnlyCollection<RemoteId> ids, CancellationToken ct = default)
    {
        const int batch = 5000;
        var missing = new List<RemoteId>();

        foreach (var page in ids.Chunk(batch))
        {
            using var r = await _http.PostAsJsonAsync($"{_base}/v1/push/{Uri.EscapeDataString(session)}/ids",
                new MissingRequest(page.Select(i => i.ToString()).ToList()),
                WireJson.Default.MissingRequest, ct).ConfigureAwait(false);

            await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
            var body = await r.Content.ReadFromJsonAsync(WireJson.Default.MissingResponse, ct).ConfigureAwait(false);
            missing.AddRange(body!.Missing.Select(x => RemoteId.Parse(x)));
        }

        return missing;
    }

    /// <summary>Asks the server to collect what no head needs. Carries the join secret, like revoke.</summary>
    public async Task<GcResponse> GcAsync(GcRequest request, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync($"{_base}/v1/gc", request,
            WireJson.Default.GcRequest, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
        return (await r.Content.ReadFromJsonAsync(WireJson.Default.GcResponse, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Withdraws a machine's token.
    /// </summary>
    /// <remarks>
    /// Carries the join secret, not this machine's token, so it works from any
    /// machine the operator can reach the server from, including a freshly
    /// installed one that has never enrolled.
    /// </remarks>
    public async Task RevokeAsync(string machineId, string joinSecret, CancellationToken ct = default)
    {
        using var r = await _http.PostAsJsonAsync($"{_base}/v1/machines/{Uri.EscapeDataString(machineId)}/revoke",
            new RevokeRequest(joinSecret), WireJson.Default.RevokeRequest, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks which of these the server lacks.
    /// </summary>
    /// <remarks>
    /// Batched, because the server caps a single question at 10 000 names and
    /// because one round trip per object would dominate the whole push.
    /// </remarks>
    public async Task<IReadOnlyList<RemoteId>> MissingAsync(
        IReadOnlyCollection<RemoteId> ids, CancellationToken ct = default)
    {
        const int batch = 5000;
        var missing = new List<RemoteId>();

        foreach (var page in ids.Chunk(batch))
        {
            using var r = await _http.PostAsJsonAsync($"{_base}/v1/objects/missing",
                new MissingRequest(page.Select(i => i.ToString()).ToList()),
                WireJson.Default.MissingRequest, ct).ConfigureAwait(false);

            await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
            var body = await r.Content.ReadFromJsonAsync(WireJson.Default.MissingResponse, ct).ConfigureAwait(false);
            missing.AddRange(body!.Missing.Select(x => RemoteId.Parse(x)));
        }

        return missing;
    }

    public async Task PutObjectAsync(RemoteId id, byte[] blob, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(blob);
        using var r = await _http.PutAsync($"{_base}/v1/objects/{id}", content, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> GetObjectAsync(RemoteId id, CancellationToken ct = default)
    {
        using var r = await _http.GetAsync($"{_base}/v1/objects/{id}", ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(r, ct).ConfigureAwait(false);
        return await r.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Turns a failed response into the reason the server gave, not a status code.</summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;

        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            var err = JsonSerializer.Deserialize(body, WireJson.Default.ApiError);
            if (err is not null) throw new RemoteException(r.StatusCode, err.Code, err.Error);
        }
        catch (JsonException)
        {
            // A proxy or a crash produced something that is not our error shape.
            // Report what actually came back rather than a generic status: on this
            // deployment a 502 from the proxy and a 502 from the app look nothing
            // alike in the body, and that difference is the whole diagnosis.
        }

        var snippet = body.Length > 200 ? body[..200] + "..." : body;
        throw new RemoteException(r.StatusCode, "http_" + (int)r.StatusCode,
            $"{(int)r.StatusCode} {r.ReasonPhrase}"
            + (string.IsNullOrWhiteSpace(snippet) ? "" : $": {snippet}"));
    }

    /// <summary>Builds the enrolment request for a machine.</summary>
    public static EnrolRequest EnrolmentFor(
        string joinSecret, Workspace.MachineIdentity machine, WorkspaceKey key)
        => new(joinSecret, machine.Id, machine.Name, machine.Platform, key.Fingerprint());
}
