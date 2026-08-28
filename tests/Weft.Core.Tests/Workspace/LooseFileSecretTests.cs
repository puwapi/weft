using Weft.Core.Git;
using Weft.Core.Store;
using Weft.Core.Tests.Support;
using Weft.Core.Workspace;

namespace Weft.Core.Tests.Workspace;

/// <summary>
/// The scanner used to run on carried patches only, so a credential sitting in a
/// file outside every repository reached the server with nothing in its way. The
/// path rules cannot cover that case: a key in 'notes.md' is in a path nobody
/// would ever have thought to list.
/// </summary>
public sealed class LooseFileSecretTests : IDisposable
{
    // The example key from AWS's own documentation: the right shape, and not a
    // credential to anything.
    private const string AwsShaped = "AKIAIOSFODNN7EXAMPLE";

    private readonly string _dir = TempTree.Create("weft-loose");
    private readonly MachineIdentity _machine = MachineIdentity.Mint("test");

    public LooseFileSecretTests() => Directory.CreateDirectory(Path.Combine(_dir, WeftRoot.MetaDir));

    public void Dispose() => TempTree.Remove(_dir);

    private void Write(string rel, string content)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    private SnapshotEngine Engine()
    {
        var root = WeftRoot.Discover(_dir);
        return new SnapshotEngine(root, new ObjectStore(root.StorePath), new GitRunner());
    }

    private Task<SnapshotResult> SnapshotAsync()
        // No repositories in these trees, so carrying is beside the point and
        // skipping it keeps the test on the one thing it is about.
        => Engine().CreateAsync(_machine, carryWork: false);

    [Fact]
    public async Task A_credential_in_a_loose_file_stops_the_snapshot()
    {
        Write("notes.md", $"reminder, the key is {AwsShaped}\n");

        var e = await Assert.ThrowsAsync<SecretsFoundException>(SnapshotAsync);

        var hit = Assert.Single(e.Findings);
        Assert.Equal("notes.md", hit.Where);
        Assert.Equal(SecretOrigin.LooseFile, hit.Origin);
        Assert.Equal("AWS access key", hit.Finding.Kind);
    }

    [Fact]
    public async Task The_secret_itself_is_not_in_the_report()
    {
        // The report goes to a terminal, into scrollback and often into a paste.
        Write("notes.md", $"key: {AwsShaped}\n");

        var e = await Assert.ThrowsAsync<SecretsFoundException>(SnapshotAsync);

        Assert.DoesNotContain(AwsShaped, e.Findings[0].Finding.Excerpt);
    }

    [Fact]
    public async Task Nothing_from_a_refused_file_reaches_the_store()
    {
        // The point of refusing. A snapshot that stops but leaves the content in
        // the store has only delayed the push, not prevented it.
        Write("notes.md", $"key: {AwsShaped}\n");

        await Assert.ThrowsAsync<SecretsFoundException>(SnapshotAsync);

        var store = new ObjectStore(Path.Combine(_dir, WeftRoot.MetaDir, "store"));
        Assert.False(store.Contains(ChunkId.Of(File.ReadAllBytes(Path.Combine(_dir, "notes.md")))));
    }

    [Fact]
    public async Task No_snapshot_is_recorded_when_a_credential_is_found()
    {
        Write("clean.txt", "nothing to see\n");
        Write("notes.md", $"key: {AwsShaped}\n");

        await Assert.ThrowsAsync<SecretsFoundException>(SnapshotAsync);

        Assert.Null(new RefStore(Path.Combine(_dir, WeftRoot.MetaDir)).ReadHead());
    }

    [Fact]
    public async Task Every_offending_file_is_named_at_once()
    {
        // Reporting one per run makes someone fix the same snapshot three times,
        // and each refusal reads as if the previous fix did not work.
        Write("a.md", $"key: {AwsShaped}\n");
        Write("deep/b.md", "-----BEGIN RSA PRIVATE KEY-----\n");

        var e = await Assert.ThrowsAsync<SecretsFoundException>(SnapshotAsync);

        Assert.Equal(2, e.Findings.Count);
        Assert.Equal(["a.md", "deep/b.md"], e.Findings.Select(f => f.Where));
    }

    [Fact]
    public async Task An_ordinary_workspace_is_recorded_normally()
    {
        // The scanner has to stay narrow. One that fires on ordinary source
        // trains people to reach for a bypass, and then it protects nothing.
        Write("src/main.cs", "var key = config[\"AWS_ACCESS_KEY\"];\nConsole.WriteLine(key);\n");
        Write("README.md", "# a project\n\nRun it with `dotnet run`.\n");

        var r = await SnapshotAsync();

        Assert.False(r.NoChange);
        Assert.Equal(2, r.FilesRead);
    }

    [Fact]
    public async Task A_credential_that_lands_after_a_clean_snapshot_is_still_caught()
    {
        // The scan runs on content the store does not already hold, so it has to
        // survive the second snapshot of a workspace, which is where a key pasted
        // in while debugging actually shows up.
        Write("notes.md", "nothing yet\n");
        Assert.False((await SnapshotAsync()).NoChange);

        Write("notes.md", $"nothing yet\nkey: {AwsShaped}\n");

        var e = await Assert.ThrowsAsync<SecretsFoundException>(SnapshotAsync);
        Assert.Equal("notes.md", Assert.Single(e.Findings).Where);
    }

    [Fact]
    public async Task Binary_content_is_left_alone()
    {
        // A false positive on a JPEG blocks a snapshot for nothing, and the
        // person cannot even see what it is complaining about.
        File.WriteAllBytes(Path.Combine(_dir, "image.bin"), [0x00, 0xFF, 0x00, 0x10, 0x42, 0x00]);

        var r = await SnapshotAsync();

        Assert.Equal(1, r.FilesRead);
    }
}
