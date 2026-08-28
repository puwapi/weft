using Weft.Core.Ignore;

namespace Weft.Core.Tests.Ignore;

public class StIgnoreImportTests
{
    [Fact]
    public void Syncthing_comments_become_weft_comments()
    {
        var r = StIgnoreReader.Parse("// dependencies\nnode_modules");
        Assert.Contains("# dependencies", r.Ignore);
        Assert.Contains("node_modules", r.Ignore);
    }

    [Fact]
    public void A_pattern_naming_a_secret_is_routed_to_the_never_set()
    {
        var r = StIgnoreReader.Parse("*.pem\n.env\nnode_modules");
        Assert.Contains("*.pem", r.Never);
        Assert.Contains(".env", r.Never);
        Assert.Contains("node_modules", r.Ignore);
    }

    [Fact]
    public void A_section_comment_marks_everything_under_it_as_confidential()
    {
        // The point of the whole heuristic. None of these three patterns names a
        // secret, and all three are confidential; the reason is in the comment,
        // which is where people actually write it down.
        var r = StIgnoreReader.Parse("""
            // Sensitive documents: never propagate these
            internal-notes/
            archive.zip
            board-minutes/
            """);

        Assert.Contains("internal-notes/", r.Never);
        Assert.Contains("archive.zip", r.Never);
        Assert.Contains("board-minutes/", r.Never);
        Assert.DoesNotContain(r.Ignore, l => !l.StartsWith('#'));
    }

    [Fact]
    public void The_confidential_section_ends_at_a_blank_line()
    {
        // Without this the first sensitive section would swallow the rest of the
        // file, and every remaining rule would land in a set the user cannot
        // override.
        var r = StIgnoreReader.Parse("""
            // Secrets
            vault/

            // Build output
            dist
            """);

        Assert.Contains("vault/", r.Never);
        Assert.Contains("dist", r.Ignore);
        Assert.DoesNotContain("dist", r.Never);
    }

    [Fact]
    public void A_French_section_comment_is_understood_too()
    {
        // Ignore files are written by people, in their own language, and the one
        // this importer was built against is French.
        var r = StIgnoreReader.Parse("""
            // Documents sensibles : ne JAMAIS les propager
            _exports/
            docs-internes/
            """);

        Assert.Contains("_exports/", r.Never);
        Assert.Contains("docs-internes/", r.Never);
    }

    [Fact]
    public void A_negated_rule_is_never_routed_to_the_confidential_set()
    {
        // '!' means re-include. Routing it to a set that forbids negation would
        // invert its meaning: the user asked to keep the file, and weft would
        // refuse it.
        var r = StIgnoreReader.Parse("""
            // Secrets
            *.key
            !public.key
            """);

        Assert.Contains("*.key", r.Never);
        Assert.Contains("!public.key", r.Ignore);
        Assert.DoesNotContain(r.Never, n => n.Contains("public.key", StringComparison.Ordinal));
    }

    [Fact]
    public void Syncthing_flag_prefixes_are_dropped_and_the_pattern_kept()
    {
        // '(?i)' and '(?d)' have no weft equivalent. Dropping the whole line
        // would silently lose a rule the user relies on, so the pattern survives
        // and the loss of the flag is reported.
        var r = StIgnoreReader.Parse("(?i)Thumbs.db\n(?d)cache/");

        Assert.Contains("Thumbs.db", r.Ignore);
        Assert.Contains("cache/", r.Ignore);
        Assert.Equal(2, r.Unsupported.Count);
    }

    [Fact]
    public void An_include_directive_is_reported_rather_than_followed()
    {
        var r = StIgnoreReader.Parse("#include other-ignores\nnode_modules");
        Assert.Contains(r.Unsupported, u => u.Contains("#include", StringComparison.Ordinal));
        Assert.DoesNotContain("#include other-ignores", r.Ignore);
    }

    [Fact]
    public void Nothing_imported_is_ever_lost()
    {
        // Every non-blank input line must come out somewhere. A rule that
        // vanishes during import is the worst outcome: the user believes their
        // exclusions carried over, and one of them did not.
        const string src = """
            // Deps
            node_modules
            dist

            // Secrets
            *.pem
            .env

            (?i)Thumbs.db
            #include more
            !keep.log
            """;

        var r = StIgnoreReader.Parse(src);
        var accounted = r.Ignore.Count(l => !l.StartsWith('#'))
                      + r.Never.Count
                      + r.Unsupported.Count(u => u.StartsWith("#include", StringComparison.OrdinalIgnoreCase));

        var inputRules = src.Split('\n')
            .Select(l => l.Trim())
            .Count(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));

        Assert.Equal(inputRules, accounted);
    }
}
