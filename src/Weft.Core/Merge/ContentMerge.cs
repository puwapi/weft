using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weft.Core.Merge;

/// <summary>How a file's content merge turned out.</summary>
public abstract record ContentResult
{
    /// <summary>A single merged version was produced.</summary>
    /// <param name="Content">What should reach disk.</param>
    /// <param name="Note">
    /// Set when the merge succeeded by a route worth telling the user about, so a
    /// resolution that was not obvious never happens silently.
    /// </param>
    public sealed record Merged(byte[] Content, string? Note = null) : ContentResult;

    /// <summary>Nothing honest can be produced. A person has to look.</summary>
    public sealed record Conflict(string Reason, IReadOnlyList<Diff3Conflict> Regions) : ContentResult;
}

/// <summary>
/// Merges the content of one file, given the common ancestor and both sides.
/// </summary>
/// <remarks>
/// <para>The order of attempts is the design. A line merge runs FIRST, even on
/// structured formats, because it preserves the file exactly as it was written:
/// its indentation, its key order, its comments. A structural merge understands
/// more but has to re-serialise, which rewrites the whole file and turns a
/// two-line change into an unreadable diff.</para>
///
/// <para>So structure is a fallback, reached only when the line merge fails. That
/// is also precisely when structure helps: two machines adding different keys to
/// the same object conflict as text and merge perfectly as data.</para>
/// </remarks>
public static class ContentMerge
{
    public static ContentResult Merge(string path, byte[] baseBytes, byte[] ours, byte[] theirs)
    {
        if (ours.AsSpan().SequenceEqual(theirs))
            return new ContentResult.Merged(ours);

        if (LooksBinary(ours) || LooksBinary(theirs) || LooksBinary(baseBytes))
            return new ContentResult.Conflict(
                "binary content cannot be merged; keep one version or the other", []);

        var text = MergeText(baseBytes, ours, theirs);
        if (text is ContentResult.Merged) return text;

        // The line merge failed. Structure may still settle it.
        var structural = TryStructural(path, baseBytes, ours, theirs);
        return structural ?? text;
    }

    // ---------- text ----------

    private static ContentResult MergeText(byte[] baseBytes, byte[] ours, byte[] theirs)
    {
        var shape = TextShape.Of(ours, theirs);

        var b = shape.Split(baseBytes);
        var o = shape.Split(ours);
        var t = shape.Split(theirs);

        // Both sides only added at the end, and neither touched a line the other
        // relies on. As lines this is a conflict, because the order of two
        // insertions at one point cannot be inferred. As an edit it is not: taking
        // both loses nothing, and appending is how notes, logs and long documents
        // actually grow.
        // A non-empty base is required, and that is not a detail. The rule is
        // justified by "neither side changed content the other relies on"; with
        // an empty base there IS no shared content, so the justification is
        // vacuous and the two files have nothing to do with each other. Two
        // machines that independently created a file of the same name have not
        // appended to anything, and stitching their contents together would
        // produce a file neither of them wrote.
        if (b.Count > 0 && IsPrefix(b, o) && IsPrefix(b, t) && o.Count > b.Count && t.Count > b.Count)
        {
            var ourTail = o.Skip(b.Count).ToList();
            var theirTail = t.Skip(b.Count).ToList();

            // Ordered by content, never by which machine is asking. Ordering by
            // "ours first" would make the two machines produce different files
            // from the same inputs, and they would conflict again forever.
            var (first, second) = string.CompareOrdinal(string.Join('\n', ourTail), string.Join('\n', theirTail)) <= 0
                ? (ourTail, theirTail)
                : (theirTail, ourTail);

            var joined = b.Concat(first).Concat(second).ToList();
            return new ContentResult.Merged(shape.Join(joined),
                "both machines appended; kept both, ordered by content");
        }

        var result = Diff3.Merge(b, o, t);

        return result.Clean
            ? new ContentResult.Merged(shape.Join(result.Lines))
            : new ContentResult.Conflict(
                $"{result.Conflicts.Count} region(s) changed differently on both machines", result.Conflicts);
    }

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> whole)
    {
        if (prefix.Count > whole.Count) return false;
        for (var i = 0; i < prefix.Count; i++) if (prefix[i] != whole[i]) return false;
        return true;
    }

    // ---------- structure ----------

    private static ContentResult? TryStructural(string path, byte[] baseBytes, byte[] ours, byte[] theirs)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        return ext switch
        {
            ".json" or ".jsonc" => TryJson(baseBytes, ours, theirs),
            ".env" or ".properties" => TryKeyValue(baseBytes, ours, theirs),
            _ when Path.GetFileName(path).StartsWith(".env", StringComparison.Ordinal)
                => TryKeyValue(baseBytes, ours, theirs),
            _ => null,
        };
    }

    private static ContentResult? TryJson(byte[] baseBytes, byte[] ours, byte[] theirs)
    {
        JsonNode? b, o, t;
        try
        {
            b = Parse(baseBytes);
            o = Parse(ours);
            t = Parse(theirs);
        }
        catch (JsonException)
        {
            // Not valid JSON after all. Fall back to the line result rather than
            // claiming a structural merge of something we cannot read.
            return null;
        }

        var merged = MergeNode(b, o, t);
        if (merged is null) return null;

        var indent = DetectIndent(ours);

        // A merged tree of null is the JSON literal, written directly.
        // JsonValue.Create((object?)null) would produce it too and is refused by
        // the AOT analyser, which is right: it needs runtime code generation, so
        // the native binary would throw here and nowhere else.
        var json = merged.Value is null
            ? "null"
            : merged.Value.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                IndentSize = indent,
            });

        return new ContentResult.Merged(
            Encoding.UTF8.GetBytes(json + "\n"),
            $"merged as JSON by key; the file was re-formatted with {indent}-space indentation");
    }

    /// <summary>
    /// A successful node merge. A wrapper rather than a bare JsonNode? because
    /// null is a legitimate JSON value, and returning it bare would make "merged
    /// to null" indistinguishable from "could not merge".
    /// </summary>
    private sealed record JsonMerge(JsonNode? Value);

    /// <summary>Three-way merge of a JSON tree. Null means the two sides genuinely disagree.</summary>
    private static JsonMerge? MergeNode(JsonNode? b, JsonNode? o, JsonNode? t)
    {
        if (Equal(o, t)) return new JsonMerge(o?.DeepClone());
        if (Equal(b, o)) return new JsonMerge(t?.DeepClone());
        if (Equal(b, t)) return new JsonMerge(o?.DeepClone());

        // Both sides changed it. Objects can still be reconciled key by key.
        if (o is JsonObject ourObject && t is JsonObject theirObject)
        {
            var baseObject = b as JsonObject;
            var result = new JsonObject();

            var names = ourObject.Select(p => p.Key)
                .Concat(theirObject.Select(p => p.Key))
                .Distinct(StringComparer.Ordinal);

            foreach (var name in names)
            {
                var inBase = baseObject is not null && baseObject.ContainsKey(name);
                var inOurs = ourObject.ContainsKey(name);
                var inTheirs = theirObject.ContainsKey(name);

                // Present in the base and gone on one side: that side removed it,
                // and a removal is a decision to honour, not an absence to fill in
                // from the other copy.
                if (inBase && (!inOurs || !inTheirs)) continue;

                baseObject?.TryGetPropertyValue(name, out var baseValue);
                JsonNode? bv = null;
                baseObject?.TryGetPropertyValue(name, out bv);

                ourObject.TryGetPropertyValue(name, out var ourValue);
                theirObject.TryGetPropertyValue(name, out var theirValue);

                var child = MergeNode(bv, inOurs ? ourValue : null, inTheirs ? theirValue : null);
                if (child is null) return null;

                result[name] = child.Value?.DeepClone();
            }

            return new JsonMerge(result);
        }

        // Arrays and scalars changed on both sides. An element-wise array merge
        // looks reasonable and is not: it reorders, duplicates, and silently
        // drops entries whose position carries meaning. Refusing is honest.
        return null;
    }

    private static bool Equal(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(a, b);
    }

    private static JsonNode? Parse(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        return JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
    }

    private static int DetectIndent(byte[] bytes)
    {
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var spaces = 0;
            while (spaces < line.Length && line[spaces] == ' ') spaces++;
            if (spaces > 0 && spaces < line.Length) return spaces;
            if (line.StartsWith('\t')) return 4;
        }
        return 2;
    }

    // ---------- key = value ----------

    private static ContentResult? TryKeyValue(byte[] baseBytes, byte[] ours, byte[] theirs)
    {
        var shape = TextShape.Of(ours, theirs);
        var b = ReadPairs(shape.Split(baseBytes));
        var o = ReadPairs(shape.Split(ours));
        var t = ReadPairs(shape.Split(theirs));

        if (b is null || o is null || t is null) return null;

        var merged = new List<string>();

        // Our key order is kept, then keys only they have. Configuration files are
        // read by people; reordering them makes every later diff unreadable.
        foreach (var key in o.Keys.Concat(t.Keys).Distinct(StringComparer.Ordinal))
        {
            b.TryGetValue(key, out var bv);
            var hasOur = o.TryGetValue(key, out var ov);
            var hasTheir = t.TryGetValue(key, out var tv);

            if (hasOur && hasTheir)
            {
                if (ov == tv) { merged.Add($"{key}={ov}"); continue; }
                if (bv == ov) { merged.Add($"{key}={tv}"); continue; }
                if (bv == tv) { merged.Add($"{key}={ov}"); continue; }
                return null;   // both set the same key to different values
            }

            // Present on one side only: removed by the other if the base had it,
            // otherwise newly added.
            var present = hasOur ? ov : tv;
            var wasInBase = b.ContainsKey(key);
            if (!wasInBase) merged.Add($"{key}={present}");
        }

        return new ContentResult.Merged(
            shape.Join(merged),
            "merged by key; comments and blank lines were not preserved");
    }

    /// <summary>Null when the text is not a plain list of key=value lines.</summary>
    private static Dictionary<string, string>? ReadPairs(IReadOnlyList<string> lines)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) return null;

            pairs[line[..eq].Trim()] = line[(eq + 1)..];
        }

        return pairs;
    }

    // ---------- shape ----------

    /// <summary>
    /// The line endings and final newline of a file, so a merge does not silently
    /// rewrite them.
    /// </summary>
    /// <remarks>
    /// Normalising every file to LF would produce a merge that touches every line
    /// of a CRLF file, which on a shared repository looks like someone rewrote it
    /// wholesale.
    /// </remarks>
    private sealed record TextShape(string Newline, bool TrailingNewline)
    {
        public static TextShape Of(byte[] ours, byte[] theirs)
        {
            var text = Encoding.UTF8.GetString(ours.Length > 0 ? ours : theirs);
            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            return new TextShape(newline, text.EndsWith('\n'));
        }

        public IReadOnlyList<string> Split(byte[] bytes)
        {
            if (bytes.Length == 0) return [];

            var text = Encoding.UTF8.GetString(bytes);
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];

            return text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }

        public byte[] Join(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return [];
            var text = string.Join(Newline, lines);
            if (TrailingNewline) text += Newline;
            return Encoding.UTF8.GetBytes(text);
        }
    }

    /// <summary>
    /// Whether content should be treated as opaque.
    /// </summary>
    /// <remarks>
    /// A NUL byte in the first few kilobytes is the test every diff tool uses. It
    /// is a heuristic, and erring towards "binary" only costs a conflict; erring
    /// the other way would line-merge an image and destroy it.
    /// </remarks>
    private static bool LooksBinary(byte[] bytes)
        => bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0;
}
