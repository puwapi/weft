using System.Security.Cryptography;
using System.Text;
using Weft.Core.Crypto;
using Weft.Core.Protocol;
using Weft.Server;

var builder = WebApplication.CreateBuilder(args);

var dataDir = builder.Configuration["Weft:DataDir"] ?? "/data";
var joinSecret = builder.Configuration["Weft:JoinSecret"] ?? "";
var minClient = builder.Configuration["Weft:MinClient"] ?? "0.0.0";
var maxObjectBytes = long.TryParse(builder.Configuration["Weft:MaxObjectBytes"], out var m)
    ? m
    : 64L * 1024 * 1024 + ChunkCipher.Overhead;

builder.Services.AddSingleton(new ServerStore(dataDir));

var app = builder.Build();
var store = app.Services.GetRequiredService<ServerStore>();

// Configuration is checked at start-up, not on the first request. A server that
// boots healthy and then refuses everything is far harder to diagnose than one
// that says why it will not start.
if (string.IsNullOrWhiteSpace(joinSecret))
{
    app.Logger.LogCritical(
        "Weft:JoinSecret is not configured. Without it no machine can enrol, so weft refuses " +
        "to start rather than run as a server nobody can join. Set the environment variable " +
        "Weft__JoinSecret (two underscores: that is how the configuration binder spells a colon).");
    return 1;
}

// Every response says what the server speaks and what it expects, so a client
// never has to guess why it was refused.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers[WeftVersion.ProtocolHeader] = WeftVersion.Protocol.ToString();
    ctx.Response.Headers[WeftVersion.MinClientHeader] = minClient;
    await next();
});

static IResult Error(int status, string code, string message)
    => Results.Json(new ApiError(message, code), WireJson.Default.ApiError, statusCode: status);

// Malformed JSON is the caller's mistake, not the server's failure.
// ReadFromJsonAsync throws on bad input, and an unhandled throw becomes a 500,
// which tells a client to retry a request that will never work and hides a real
// fault behind the same status. Found by a probe sending a truncated body.
static async Task<(T? Value, IResult? Error)> ReadBodyAsync<T>(
    HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
{
    try
    {
        var value = await ctx.Request.ReadFromJsonAsync(type);
        return value is null
            ? (default, Error(StatusCodes.Status400BadRequest, "bad_request", "Expected a JSON body."))
            : (value, null);
    }
    catch (System.Text.Json.JsonException e)
    {
        return (default, Error(StatusCodes.Status400BadRequest, "bad_json",
            $"The request body is not valid JSON: {e.Message}"));
    }
    catch (BadHttpRequestException e)
    {
        return (default, Error(StatusCodes.Status400BadRequest, "bad_request", e.Message));
    }
}

// Constant-time: a byte-by-byte comparison that stops at the first difference
// leaks the secret's prefix through timing, one character at a time.
bool JoinSecretOk(string? provided)
{
    var a = Encoding.UTF8.GetBytes(provided ?? "");
    var b = Encoding.UTF8.GetBytes(joinSecret);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

Machine? Authenticate(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
    return store.Authenticate(header["Bearer ".Length..].Trim());
}

/// Writes are refused to a build below the floor; reads are not.
/// Blocking reads too would strand an outdated machine with no way to fetch what
/// it needs, including its own work. A stale machine is a smaller problem than an
/// isolated one.
bool WriteAllowed(HttpContext ctx, out IResult? refusal)
{
    refusal = null;
    var client = ctx.Request.Headers["Weft-Client"].ToString();

    if (Version.TryParse(minClient, out var floor)
        && (!Version.TryParse(client, out var actual) || actual < floor))
    {
        refusal = Error(StatusCodes.Status426UpgradeRequired, "client_too_old",
            $"This server accepts writes from weft {minClient} or later; you are running "
            + (string.IsNullOrEmpty(client) ? "an unknown version" : client) + ". Run 'weft up'.");
        return false;
    }

    return true;
}

// ---------- open ----------

app.MapGet("/health", () => Results.Text("ok"));

app.MapGet("/v1/info", () => Results.Json(
    new ServerInfo(WeftVersion.Protocol, minClient, WeftVersion.Build, store.Fingerprint() ?? ""),
    WireJson.Default.ServerInfo));

app.MapPost("/v1/enrol", async (HttpContext ctx) =>
{
    var (req, bodyError) = await ReadBodyAsync(ctx, WireJson.Default.EnrolRequest);
    if (bodyError is not null) return bodyError;

    if (!JoinSecretOk(req!.JoinSecret))
        return Error(StatusCodes.Status403Forbidden, "bad_join_secret", "Join secret rejected.");

    if (string.IsNullOrWhiteSpace(req.MachineId) || string.IsNullOrWhiteSpace(req.WorkspaceFingerprint))
        return Error(StatusCodes.Status400BadRequest, "bad_request", "Machine id and workspace fingerprint are required.");

    if (!store.ClaimOrMatchFingerprint(req.WorkspaceFingerprint))
        return Error(StatusCodes.Status409Conflict, "wrong_workspace",
            "This server already holds a different workspace. A machine with another key would upload "
            + "objects nobody here can read. Point it at a different server, or check the key it was given.");

    var (machine, token) = store.Enrol(req.MachineId, req.MachineName, req.Platform);
    return Results.Json(new EnrolResponse(token, machine.Id), WireJson.Default.EnrolResponse);
});

// Withdraws a machine's token. Guarded by the join secret and NOT by a bearer
// token, which is the point: the machine being revoked is usually the one that
// was lost, and it holds a token. It does not hold the join secret, because the
// client exchanges that for a token at enrolment and never writes it down.
// Accepting a token here would let whoever took the machine revoke everyone else.
app.MapPost("/v1/machines/{id}/revoke", async (string id, HttpContext ctx) =>
{
    var (req, bodyError) = await ReadBodyAsync(ctx, WireJson.Default.RevokeRequest);
    if (bodyError is not null) return bodyError;

    // The secret is checked before the machine is looked up. The other order
    // answers "does this machine exist?" to anyone who asks, which is a question
    // an unauthenticated caller has no business getting an answer to.
    if (!JoinSecretOk(req!.JoinSecret))
        return Error(StatusCodes.Status403Forbidden, "bad_join_secret", "Join secret rejected.");

    if (!store.Revoke(id))
        return Error(StatusCodes.Status404NotFound, "no_such_machine",
            "No machine by that id is currently enrolled. Run 'weft remote machines' for the list.");

    return Results.NoContent();
});

// ---------- authenticated ----------

app.MapGet("/v1/heads", (HttpContext ctx) =>
{
    if (Authenticate(ctx) is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Bad or missing token.");

    return Results.Json(
        new HeadsResponse(store.AllMachines().Select(store.ToHead).ToList()),
        WireJson.Default.HeadsResponse);
});

app.MapPut("/v1/head", async (HttpContext ctx) =>
{
    var me = Authenticate(ctx);
    if (me is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Bad or missing token.");
    if (!WriteAllowed(ctx, out var refusal)) return refusal!;

    var (req, bodyError) = await ReadBodyAsync(ctx, WireJson.Default.SetHeadRequest);
    if (bodyError is not null) return bodyError;
    if (!RemoteId.TryParse(req!.Snapshot, out _))
        return Error(StatusCodes.Status400BadRequest, "bad_request", "Expected an object id.");

    if (!store.HasObject(req.Snapshot))
        return Error(StatusCodes.Status409Conflict, "snapshot_missing",
            "That snapshot is not on the server. Push its objects before moving the pointer, "
            + "or other machines will follow a pointer into nothing.");

    // The machine comes from the token, never from the request. This is the whole
    // of "a machine writes only within its own namespace": there is no parameter
    // that could name another machine's pointer.
    store.SetHead(me.Id, req.Snapshot);
    return Results.NoContent();
});

app.MapPost("/v1/objects/missing", async (HttpContext ctx) =>
{
    if (Authenticate(ctx) is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Bad or missing token.");

    var (req, bodyError) = await ReadBodyAsync(ctx, WireJson.Default.MissingRequest);
    if (bodyError is not null) return bodyError;

    if (req!.Ids.Count > 10_000)
        return Error(StatusCodes.Status400BadRequest, "too_many",
            "Ask about at most 10 000 objects at a time.");

    var missing = req!.Ids.Where(id => RemoteId.TryParse(id, out _) && !store.HasObject(id)).ToList();
    return Results.Json(new MissingResponse(missing), WireJson.Default.MissingResponse);
});

app.MapPut("/v1/objects/{id}", async (string id, HttpContext ctx) =>
{
    if (Authenticate(ctx) is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Bad or missing token.");
    if (!WriteAllowed(ctx, out var refusal)) return refusal!;

    if (!RemoteId.TryParse(id, out _))
        return Error(StatusCodes.Status400BadRequest, "bad_id", "Not an object id.");

    try
    {
        var written = await store.PutObjectAsync(id, ctx.Request.Body, maxObjectBytes, ctx.RequestAborted);

        // Already present is success, not a conflict: two machines pushing the
        // same content is the normal case, and an error would make the client
        // treat a healthy push as a failure.
        return written ? Results.Created($"/v1/objects/{id}", null) : Results.NoContent();
    }
    catch (InvalidOperationException e)
    {
        return Error(StatusCodes.Status413PayloadTooLarge, "too_large", e.Message);
    }
});

app.MapGet("/v1/objects/{id}", (string id, HttpContext ctx) =>
{
    if (Authenticate(ctx) is null) return Error(StatusCodes.Status401Unauthorized, "unauthenticated", "Bad or missing token.");

    if (!RemoteId.TryParse(id, out _))
        return Error(StatusCodes.Status400BadRequest, "bad_id", "Not an object id.");

    var stream = store.OpenObject(id);
    return stream is null
        ? Error(StatusCodes.Status404NotFound, "not_found", "No such object.")
        : Results.Stream(stream, "application/octet-stream");
});

app.Logger.LogInformation(
    "weft server {Build}, protocol {Protocol}, data in {DataDir}, writes require client >= {MinClient}",
    WeftVersion.Build, WeftVersion.Protocol, dataDir, minClient);

app.Run();
return 0;
