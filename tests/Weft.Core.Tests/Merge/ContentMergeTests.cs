using System.Text;
using System.Text.Json.Nodes;
using Weft.Core.Merge;

namespace Weft.Core.Tests.Merge;

public class ContentMergeTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    private static ContentResult.Merged Clean(string path, string b, string o, string t)
    {
        var r = ContentMerge.Merge(path, B(b), B(o), B(t));
        return Assert.IsType<ContentResult.Merged>(r);
    }

    private static ContentResult.Conflict Conflicted(string path, string b, string o, string t)
    {
        var r = ContentMerge.Merge(path, B(b), B(o), B(t));
        return Assert.IsType<ContentResult.Conflict>(r);
    }

    // ---------- text ----------

    [Fact]
    public void Identical_sides_never_reach_the_merge_at_all()
        => Assert.Equal("same\n", S(Clean("a.txt", "base\n", "same\n", "same\n").Content));

    [Fact]
    public void Edits_in_different_places_merge_cleanly()
    {
        var r = Clean("code.cs",
            "one\ntwo\nthree\nfour\nfive\nsix\n",
            "ONE\ntwo\nthree\nfour\nfive\nsix\n",
            "one\ntwo\nthree\nfour\nfive\nSIX\n");

        Assert.Equal("ONE\ntwo\nthree\nfour\nfive\nSIX\n", S(r.Content));
        Assert.Null(r.Note);
    }

    [Fact]
    public void The_same_line_edited_differently_conflicts()
    {
        var c = Conflicted("code.cs", "a\nb\nc\n", "a\nOURS\nc\n", "a\nTHEIRS\nc\n");
        Assert.Single(c.Regions);
        Assert.Contains("both machines", c.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_appends_are_both_kept_and_the_user_is_told()
    {
        // The motivating case: a long document that grows by appending sections,
        // edited on two machines. As lines this is a conflict; as an edit it is
        // not, because taking both loses nothing.
        var r = Clean("notes.md",
            "# Notes\n\n## One\n",
            "# Notes\n\n## One\n\n## Two\n",
            "# Notes\n\n## One\n\n## Three\n");

        var text = S(r.Content);
        Assert.Contains("## Two", text, StringComparison.Ordinal);
        Assert.Contains("## Three", text, StringComparison.Ordinal);
        Assert.NotNull(r.Note);
        Assert.Contains("appended", r.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_appends_produce_the_SAME_file_on_both_machines()
    {
        // The property that makes appending converge. Ordering by "ours first"
        // would give each machine a different file from the same inputs, and they
        // would conflict again on the next round, forever.
        const string b = "shared\n";
        const string o = "shared\nfrom A\n";
        const string t = "shared\nfrom B\n";

        var onA = Clean("notes.md", b, o, t).Content;
        var onB = Clean("notes.md", b, t, o).Content;   // sides swapped

        Assert.Equal(S(onA), S(onB));
    }

    [Fact]
    public void An_append_on_one_side_and_an_edit_on_the_other_is_not_treated_as_two_appends()
    {
        // The union rule is only safe when NEITHER side changed existing content.
        var r = ContentMerge.Merge("notes.md",
            B("a\nb\n"), B("a\nb\nappended\n"), B("a\nCHANGED\n"));

        var merged = Assert.IsType<ContentResult.Merged>(r);
        Assert.Null(merged.Note);   // merged by diff3, not by the union rule
        Assert.Contains("CHANGED", S(merged.Content), StringComparison.Ordinal);
        Assert.Contains("appended", S(merged.Content), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_independently_created_files_are_NOT_stitched_together()
    {
        // The union rule needs a shared base to be justified by. Without one the
        // two files have nothing to do with each other, and concatenating them
        // would produce a file neither machine wrote.
        Conflicted("notes.md", "", "written on A\n", "written on B\n");
    }

    [Fact]
    public void Windows_line_endings_are_not_rewritten()
    {
        // Normalising to LF would make the merge touch every line of the file,
        // which on a shared repository looks like someone rewrote it wholesale.
        var r = Clean("win.txt", "a\r\nb\r\nc\r\n", "A\r\nb\r\nc\r\n", "a\r\nb\r\nC\r\n");
        Assert.Contains("\r\n", S(r.Content), StringComparison.Ordinal);
        Assert.Equal("A\r\nb\r\nC\r\n", S(r.Content));
    }

    [Fact]
    public void A_missing_final_newline_stays_missing()
    {
        var r = Clean("a.txt", "a\nb", "A\nb", "a\nB");
        Assert.False(S(r.Content).EndsWith('\n'));
    }

    // ---------- binary ----------

    [Fact]
    public void Binary_content_is_refused_rather_than_line_merged()
    {
        // Line-merging an image destroys it, and the result would look like a
        // successful merge.
        var b = new byte[] { 1, 2, 0, 3, 4 };
        var o = new byte[] { 1, 2, 0, 9, 4 };
        var t = new byte[] { 1, 2, 0, 3, 7 };

        var c = Assert.IsType<ContentResult.Conflict>(ContentMerge.Merge("img.png", b, o, t));
        Assert.Contains("binary", c.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identical_binary_content_is_still_fine()
    {
        var same = new byte[] { 1, 0, 2, 0, 3 };
        Assert.IsType<ContentResult.Merged>(
            ContentMerge.Merge("img.png", new byte[] { 0, 9 }, same, (byte[])same.Clone()));
    }

    // ---------- JSON ----------

    [Fact]
    public void Two_keys_added_at_the_same_spot_merge_as_JSON()
    {
        // Conflicts as text, merges perfectly as data. This is the case the
        // structural fallback exists for.
        var r = Clean("package.json",
            "{\n  \"name\": \"app\"\n}\n",
            "{\n  \"name\": \"app\",\n  \"ours\": \"1.0.0\"\n}\n",
            "{\n  \"name\": \"app\",\n  \"theirs\": \"2.0.0\"\n}\n");

        var node = JsonNode.Parse(S(r.Content))!.AsObject();
        Assert.Equal("app", (string?)node["name"]);
        Assert.Equal("1.0.0", (string?)node["ours"]);
        Assert.Equal("2.0.0", (string?)node["theirs"]);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Nested_objects_merge_key_by_key()
    {
        var r = Clean("messages.json",
            """{"app":{"title":"T"}}""",
            """{"app":{"title":"T","save":"Save"}}""",
            """{"app":{"title":"T","cancel":"Cancel"}}""");

        var app = JsonNode.Parse(S(r.Content))!["app"]!.AsObject();
        Assert.Equal("Save", (string?)app["save"]);
        Assert.Equal("Cancel", (string?)app["cancel"]);
    }

    [Fact]
    public void The_same_key_set_to_two_different_values_still_conflicts()
    {
        // Structure cannot decide this, and pretending otherwise would pick a
        // winner silently.
        Conflicted("config.json",
            """{"port":80}""", """{"port":8080}""", """{"port":9090}""");
    }

    [Fact]
    public void A_key_removed_on_one_side_stays_removed()
    {
        var r = Clean("config.json",
            """{"keep":1,"drop":2}""",
            """{"keep":1}""",
            """{"keep":1,"drop":2,"added":3}""");

        var o = JsonNode.Parse(S(r.Content))!.AsObject();
        Assert.False(o.ContainsKey("drop"));
        Assert.True(o.ContainsKey("added"));
    }

    [Fact]
    public void Arrays_changed_on_both_sides_are_refused()
    {
        // An element-wise array merge reorders, duplicates and drops entries whose
        // position carries meaning. Refusing is the honest answer.
        Conflicted("config.json",
            """{"list":[1,2]}""", """{"list":[1,2,3]}""", """{"list":[1,2,4]}""");
    }

    [Fact]
    public void Malformed_JSON_falls_back_to_the_text_result()
    {
        // Claiming a structural merge of something unparseable would be worse
        // than reporting the line conflict that was already found.
        var c = Conflicted("broken.json", "{oops", "{ours", "{theirs");
        Assert.DoesNotContain("JSON", c.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Well_formed_JSON_that_merges_as_TEXT_keeps_its_formatting()
    {
        // The line merge runs first precisely so this happens: the file keeps its
        // key order, its indentation and its comments.
        var r = Clean("cfg.json",
            "{\n    \"a\": 1,\n    \"b\": 2,\n    \"c\": 3\n}\n",
            "{\n    \"a\": 9,\n    \"b\": 2,\n    \"c\": 3\n}\n",
            "{\n    \"a\": 1,\n    \"b\": 2,\n    \"c\": 9\n}\n");

        Assert.Contains("    \"a\": 9", S(r.Content), StringComparison.Ordinal);
        Assert.Null(r.Note);   // no structural fallback, so no re-formatting
    }

    // ---------- key = value ----------

    [Fact]
    public void Different_variables_added_to_an_env_file_merge()
    {
        var r = Clean(".env",
            "SHARED=1\n", "SHARED=1\nOURS=a\n", "SHARED=1\nTHEIRS=b\n");

        var text = S(r.Content);
        Assert.Contains("OURS=a", text, StringComparison.Ordinal);
        Assert.Contains("THEIRS=b", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_variable_set_to_two_values_conflicts()
        => Conflicted(".env", "PORT=80\n", "PORT=8080\n", "PORT=9090\n");

    [Fact]
    public void An_env_variant_filename_is_recognised()
    {
        var r = Clean(".env.production",
            "A=1\n", "A=1\nB=2\n", "A=1\nC=3\n");
        Assert.Contains("C=3", S(r.Content), StringComparison.Ordinal);
    }
}
