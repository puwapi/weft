using System.Text;
using Weft.Core.Safety;

namespace Weft.Core.Tests.Safety;

public class SecretScannerTests
{
    // Every fixture below is deliberately shaped like a real credential and
    // deliberately not one: 'EXAMPLENOTAREAL' filler instead of the random
    // characters an issuer would emit.
    //
    // The first version of this file used realistic-looking values, and GitHub's
    // own push protection refused the commit. It was right to: a string that a
    // scanner cannot distinguish from a live key does not belong in a repository,
    // whatever the author meant by it. The fixtures still exercise every pattern.

    private static IReadOnlyList<SecretFinding> Scan(string text)
        => SecretScanner.Scan(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private key")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", "private key")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE", "AWS access key")]
    [InlineData("token: ghp_EXAMPLENOTAREALTOKEN000000000000000000", "GitHub token")]
    [InlineData("STRIPE=sk_live_EXAMPLENOTAREALKEY0000", "Stripe live key")]
    [InlineData("slack: xoxb-EXAMPLE-NOT-A-REAL-TOKEN", "Slack token")]
    [InlineData("key=AIzaEXAMPLENOTAREALKEY00000000000000000", "Google API key")]
    [InlineData("ANTHROPIC_API_KEY=sk-ant-EXAMPLENOTAREALKEY00", "Anthropic key")]
    [InlineData("DATABASE_URL=postgres://admin:hunter2secret@db.example.com/app", "connection string with password")]
    public void A_real_looking_credential_is_caught(string line, string kind)
    {
        var f = Assert.Single(Scan(line));
        Assert.Equal(kind, f.Kind);
        Assert.Equal(1, f.Line);
    }

    [Theory]
    // The false positives that matter. A scanner that fires on ordinary code
    // trains people to pass --force by reflex, and one everyone bypasses costs
    // friction and buys nothing.
    [InlineData("const apiKey = process.env.STRIPE_KEY;")]
    [InlineData("// Set STRIPE_SECRET_KEY before running")]
    [InlineData("DATABASE_URL=postgres://user@localhost:5432/app")]
    [InlineData("DATABASE_URL=postgres://user:password@localhost/app")]
    [InlineData("DATABASE_URL=postgres://user:changeme@localhost/app")]
    [InlineData("DATABASE_URL=postgres://user:<password>@host/db")]
    [InlineData("REDIS_URL=redis://localhost:6379")]
    [InlineData("private_key_path = \"/etc/ssl/private/server.key\"")]
    [InlineData("export function getPrivateKey(): string { return read(); }")]
    [InlineData("sk-")]
    [InlineData("AKIA")]
    [InlineData("password = os.environ[\"DB_PASSWORD\"]")]
    [InlineData("Authorization: Bearer ${token}")]
    // A key of the wrong length is not that key. Google issues 39 characters;
    // matching a shorter or longer run would catch identifiers that merely start
    // the same way.
    [InlineData("key=AIzaTooShort")]
    [InlineData("key=AIzaEXAMPLENOTAREALKEY00000000000000000000")]
    public void Ordinary_code_and_documentation_are_left_alone(string line)
        => Assert.Empty(Scan(line));

    [Fact]
    public void The_secret_itself_never_reaches_the_message()
    {
        // The report goes to a terminal, into scrollback, and often into a paste.
        // Saying "you are about to leak this key" by printing the key is its own
        // leak.
        const string secret = "ghp_EXAMPLENOTAREALTOKEN000000000000000000";
        var f = Assert.Single(Scan($"token: {secret}"));

        Assert.DoesNotContain(secret, f.Excerpt, StringComparison.Ordinal);
        Assert.Contains("token:", f.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_number_points_at_the_right_line()
    {
        var f = Assert.Single(Scan("one\ntwo\n-----BEGIN RSA PRIVATE KEY-----\nfour\n"));
        Assert.Equal(3, f.Line);
    }

    [Fact]
    public void Several_secrets_are_all_reported()
        => Assert.Equal(2, Scan("AKIAIOSFODNN7EXAMPLE\nghp_EXAMPLENOTAREALTOKEN000000000000000000\n").Count);

    [Fact]
    public void Binary_content_is_skipped_entirely()
    {
        // A false positive on a compiled binary or a JPEG helps nobody, and the
        // excerpt would be unreadable.
        var bytes = Encoding.UTF8.GetBytes("AKIAIOSFODNN7EXAMPLE").Concat<byte>([0, 1, 2]).ToArray();
        Assert.Empty(SecretScanner.Scan(bytes));
    }

    [Fact]
    public void A_minified_bundle_is_not_scanned_line_by_line()
    {
        // One enormous line is a build artefact, not a place secrets hide, and
        // scanning it would dominate the cost of every snapshot.
        var huge = new string('x', 5000) + " AKIAIOSFODNN7EXAMPLE";
        Assert.Empty(Scan(huge));
    }

    [Fact]
    public void An_empty_input_finds_nothing()
        => Assert.Empty(SecretScanner.Scan([]));

    [Fact]
    public void A_patch_carrying_a_key_on_an_added_line_is_caught()
    {
        // The realistic shape: a key pasted into a source file while debugging,
        // arriving as part of a diff. No path-based rule could ever have covered
        // that file.
        const string patch = """
            diff --git a/src/config.ts b/src/config.ts
            index 1234567..89abcde 100644
            --- a/src/config.ts
            +++ b/src/config.ts
            @@ -1,3 +1,4 @@
             export const config = {
            +  stripe: "sk_live_EXAMPLENOTAREALKEY0000",
             };
            """;

        Assert.Single(Scan(patch), f => f.Kind == "Stripe live key");
    }
}
