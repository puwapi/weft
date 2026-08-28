using Weft.Core.Tests.Support;

namespace Weft.Core.Tests.Support;

public class TempTreeTests
{
    [Fact]
    public void A_tree_holding_read_only_files_is_still_removed()
    {
        // git marks its loose objects read-only. On Windows a recursive delete
        // respects that and throws; on Unix the parent directory's write bit
        // decides and the same call succeeds. Every test that puts a real
        // repository in a temp directory depends on this working on both.
        var root = TempTree.Create("weft-readonly-probe");
        var nested = Path.Combine(root, "objects", "ab");
        Directory.CreateDirectory(nested);

        var file = Path.Combine(nested, "cdef1234");
        File.WriteAllText(file, "an object");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        TempTree.Remove(root);

        Assert.False(Directory.Exists(root), "the tree survived, so a read-only file blocked the delete");
    }

    [Fact]
    public void Removing_something_that_is_not_there_is_not_an_error()
        => TempTree.Remove(Path.Combine(Path.GetTempPath(), "weft-never-existed-" + Guid.NewGuid().ToString("n")));

    [Fact]
    public void Created_directories_do_not_collide()
    {
        var a = TempTree.Create("weft-probe");
        var b = TempTree.Create("weft-probe");

        try { Assert.NotEqual(a, b); }
        finally { TempTree.Remove(a); TempTree.Remove(b); }
    }
}
