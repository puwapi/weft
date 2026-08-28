using Weft.Core.Ignore;

namespace Weft.Core.Tests.Ignore;

public class IgnorePolicyTests
{
    private static IgnorePolicy Policy(string ignore = "", string never = "")
        => IgnorePolicy.Parse(ignore, never);

    [Fact]
    public void Unmatched_path_is_included()
        => Assert.Equal(IgnoreVerdict.Include,
            Policy("node_modules").Match("hub/web/lib/api.ts", false));

    [Fact]
    public void Later_rule_wins_over_earlier_one()
    {
        var p = Policy("*.log\n!keep.log");
        Assert.Equal(IgnoreVerdict.Ignored, p.Match("server.log", false));
        Assert.Equal(IgnoreVerdict.Include, p.Match("keep.log", false));
    }

    [Fact]
    public void Never_beats_an_ignore_negation()
    {
        // The user re-includes every '.env'; the confidentiality set still refuses.
        var p = Policy(ignore: "!.env", never: ".env");
        Assert.Equal(IgnoreVerdict.Never, p.Match(".env", false));
    }

    [Fact]
    public void Never_beats_rule_order_regardless_of_position()
    {
        var p = Policy(ignore: "!secrets.pem\n!*.pem\n!**/*.pem", never: "*.pem");
        Assert.Equal(IgnoreVerdict.Never, p.Match("infra/server/secrets.pem", false));
    }

    [Fact]
    public void Negation_is_refused_in_the_never_set_rather_than_dropped()
    {
        // Dropping it silently would let someone believe an exception applied.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Policy(never: "*.pem\n!public.pem"));
        Assert.Contains("confidentiality boundary", ex.Message);
    }

    [Theory]
    [InlineData("=*.pem")]
    [InlineData("=.env.*")]
    [InlineData("=id_?sa")]
    [InlineData("=[a-z].key")]
    public void An_exemption_may_not_be_a_pattern(string line)
    {
        // A literal exemption can only ever spare one filename. A pattern could
        // spare a whole class, which is negation wearing a different hat, and
        // negation is what the never set exists to refuse.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Policy(never: $"*.pem\n.env.*\n{line}"));
        Assert.Contains("literal name", ex.Message);
    }

    [Fact]
    public void An_exemption_spares_exactly_one_name_and_nothing_near_it()
    {
        var p = Policy(never: "*.pem\n=public.pem");

        // The exempted name passes, at any depth.
        Assert.Equal(IgnoreVerdict.Include, p.Match("certs/public.pem", false));
        Assert.Equal(IgnoreVerdict.Include, p.Match("public.pem", false));

        // Every other name in the class is still refused. The exemption spares a
        // name, not a prefix and not a neighbourhood.
        Assert.Equal(IgnoreVerdict.Never, p.Match("certs/publicX.pem", false));
        Assert.Equal(IgnoreVerdict.Never, p.Match("certs/private.pem", false));
        Assert.Equal(IgnoreVerdict.Never, p.Match("certs/public-2.pem", false));
    }

    [Fact]
    public void Ignored_directory_is_not_descended_into()
    {
        var p = Policy("node_modules");
        Assert.False(p.ShouldDescend("hub/web/node_modules"));
        Assert.True(p.ShouldDescend("hub/web/lib"));
    }

    [Fact]
    public void Confidential_directory_is_not_descended_into_either()
    {
        var p = Policy(never: "docs-internes/");
        Assert.False(p.ShouldDescend("docs-internes"));
    }

    [Fact]
    public void Directory_only_rule_does_not_prune_a_file_of_the_same_name()
    {
        var p = Policy("build/");
        Assert.False(p.ShouldDescend("build"));
        Assert.Equal(IgnoreVerdict.Include, p.Match("build", isDirectory: false));
    }

    [Fact]
    public void Comments_and_blank_lines_are_not_rules()
    {
        var p = Policy("# node_modules\n\n   \n*.tmp");
        Assert.Equal(IgnoreVerdict.Include, p.Match("node_modules", true));
        Assert.Equal(IgnoreVerdict.Ignored, p.Match("a.tmp", false));
    }

    [Theory]
    // The confidentiality classes that must hold on the reference tree.
    [InlineData("infra/server/id_ed25519")]
    [InlineData("certs/oidc/signing.pfx")]
    [InlineData("hub/api/.env")]
    [InlineData("hub/api/.env.local")]
    [InlineData("certs/developerID_application.cer")]
    [InlineData("infra/.aws/credentials")]
    public void Default_confidential_classes_are_refused(string path)
        => Assert.Equal(IgnoreVerdict.Never,
            IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never).Match(path, false));

    [Theory]
    // The regenerable classes measured on the reference tree: these six account
    // for 53 GB of the 54 GB on disk.
    [InlineData("hub/web/node_modules")]
    [InlineData("hub/web/.next")]
    [InlineData("work/api/Puwapi.Work.API/bin")]
    [InlineData("work/api/Puwapi.Work.API/obj")]
    [InlineData("training-vr-app/Library")]
    [InlineData("mcp/dist")]
    public void Default_regenerable_classes_are_pruned(string dir)
        => Assert.False(IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never).ShouldDescend(dir));

    [Fact]
    public void Defaults_do_not_carry_project_specific_patterns()
    {
        // The defaults cover universal classes only. Project-specific documents
        // go in the user's own .weftnever: a default list that tries to guess
        // what a stranger considers sensitive grows without bound and still
        // misses the case that matters.
        Assert.DoesNotContain("RGPD", DefaultRules.Never, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mailfinder", DefaultRules.Never, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Env_example_survives_a_rule_that_refuses_every_other_env_file()
    {
        var p = IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never);

        Assert.Equal(IgnoreVerdict.Never, p.Match("forms/.env", false));
        Assert.Equal(IgnoreVerdict.Never, p.Match("forms/.env.production", false));
        Assert.Equal(IgnoreVerdict.Never, p.Match("forms/.env.local", false));

        // Documents variable names, holds no secret, and is committed everywhere.
        Assert.Equal(IgnoreVerdict.Include, p.Match("forms/.env.example", false));
        Assert.Equal(IgnoreVerdict.Include, p.Match(".env.sample", false));
    }

    [Fact]
    public void A_repository_is_not_placed_in_the_never_set_by_default()
    {
        // Deliberate, and worth stating so it is not "fixed" by someone later.
        //
        // A byte-level synchroniser has to exclude a sensitive directory, because
        // it copies every byte to every machine in the mesh. weft does not: for a
        // git repository it syncs state and missing objects, and the content
        // already has a remote of its own. Marking a repository 'never' would only
        // stop weft from knowing it exists, buying no confidentiality.
        //
        // A user who wants it excluded adds the line. That is their call, not a
        // default.
        var p = IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never);
        Assert.NotEqual(IgnoreVerdict.Never, p.Match("docs-internes/architecture.md", false));
    }

    [Fact]
    public void Source_that_looks_like_build_output_is_still_kept()
    {
        var p = IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never);

        // Unity regenerates these, but they are the source of record for the
        // .NET modules and excluding them would break every build.
        Assert.Equal(IgnoreVerdict.Include, p.Match("work/api/Puwapi.Work.API.csproj", false));
        Assert.Equal(IgnoreVerdict.Include, p.Match("work/api/Puwapi.Work.slnx", false));

        // '.env.example' documents the variables and is committed on purpose.
        Assert.Equal(IgnoreVerdict.Include, p.Match("forms/.env.example", false));
    }
}
