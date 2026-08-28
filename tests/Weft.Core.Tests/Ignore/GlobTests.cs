using Weft.Core.Ignore;

namespace Weft.Core.Tests.Ignore;

public class GlobTests
{
    [Theory]
    // A pattern with no '/' matches at any depth. This is what makes a single
    // 'node_modules' line prune all 56 occurrences in the reference tree.
    [InlineData("node_modules", "node_modules", true)]
    [InlineData("node_modules", "hub/web/node_modules", true)]
    [InlineData("node_modules", "a/b/c/d/node_modules", true)]
    [InlineData("node_modules", "hub/web/node_modules_backup", false)]

    // A pattern containing '/' is anchored at the root.
    [InlineData("training-vr-app/Library", "training-vr-app/Library", true)]
    [InlineData("training-vr-app/Library", "other/training-vr-app/Library", false)]
    [InlineData("/build", "build", true)]
    [InlineData("/build", "src/build", false)]

    // '*' stops at a separator, '**' crosses them.
    [InlineData("*.log", "server.log", true)]
    [InlineData("*.log", "logs/server.log", true)]
    [InlineData("src/*.cs", "src/Program.cs", true)]
    [InlineData("src/*.cs", "src/deep/Program.cs", false)]
    [InlineData("src/**/*.cs", "src/deep/nested/Program.cs", true)]
    [InlineData("src/**/*.cs", "src/Program.cs", true)]

    // '?' and character classes.
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file10.txt", false)]
    [InlineData("*.[oa]", "libfoo.o", true)]
    [InlineData("*.[oa]", "libfoo.a", true)]
    [InlineData("*.[oa]", "libfoo.c", false)]
    [InlineData("*.[!oa]", "libfoo.c", true)]
    [InlineData("[a-c]*.txt", "b1.txt", true)]
    [InlineData("[a-c]*.txt", "z1.txt", false)]
    public void Matches_path(string pattern, string path, bool expected)
    {
        var g = Glob.Parse(pattern);
        Assert.NotNull(g);
        Assert.Equal(expected, g.IsMatch(path, isDirectory: false));
    }

    [Fact]
    public void Trailing_slash_restricts_to_directories()
    {
        var g = Glob.Parse("build/")!;
        Assert.True(g.DirectoryOnly);
        Assert.True(g.IsMatch("build", isDirectory: true));
        Assert.False(g.IsMatch("build", isDirectory: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("/")]
    public void Blank_and_comment_lines_produce_no_rule(string line)
        => Assert.Null(Glob.Parse(line));

    [Fact]
    public void Star_backtracks_rather_than_matching_greedily_and_failing()
    {
        // A naive left-to-right '*' consumes to the end and then fails on '.ts'.
        var g = Glob.Parse("*.test.ts")!;
        Assert.True(g.IsMatch("api.test.ts", isDirectory: false));
        Assert.True(g.IsMatch("a.b.c.test.ts", isDirectory: false));
        Assert.False(g.IsMatch("api.test.tsx", isDirectory: false));
    }

    [Fact]
    public void DoubleStar_matches_zero_segments()
    {
        var g = Glob.Parse("src/**/index.ts")!;
        Assert.True(g.IsMatch("src/index.ts", isDirectory: false));
        Assert.True(g.IsMatch("src/a/index.ts", isDirectory: false));
        Assert.True(g.IsMatch("src/a/b/index.ts", isDirectory: false));
    }

    [Fact]
    public void Unterminated_character_class_is_a_literal_bracket()
    {
        var g = Glob.Parse("weird[name")!;
        Assert.True(g.IsMatch("weird[name", isDirectory: false));
    }
}
