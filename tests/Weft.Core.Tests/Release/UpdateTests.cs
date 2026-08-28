using System.Text;
using Weft.Core.Release;
using Weft.Core.Tests.Support;

namespace Weft.Core.Tests.Release;

public class ReleaseTargetTests
{
    [Theory]
    [InlineData("macos", "arm64", "weft-macos-arm64")]
    [InlineData("macos", "x64", "weft-macos-x64")]
    [InlineData("linux", "x64", "weft-linux-x64")]
    [InlineData("linux", "arm64", "weft-linux-arm64")]
    [InlineData("windows", "x64", "weft-windows-x64.exe")]
    [InlineData("windows", "arm64", "weft-windows-arm64.exe")]
    public void The_name_matches_what_the_release_publishes(string os, string arch, string expected)
    {
        // Pinned against the six names in .github/workflows/release.yml. A rename
        // on either side should break this test rather than leave 'weft up'
        // downloading a 404 on one platform only.
        Assert.Equal(expected, ReleaseTarget.AssetName(os, arch));
    }

    [Theory]
    [InlineData("linux", "x86_64", "weft-linux-x64")]
    [InlineData("linux", "amd64", "weft-linux-x64")]
    [InlineData("linux", "aarch64", "weft-linux-arm64")]
    public void The_spellings_a_machine_might_report_are_understood(string os, string arch, string expected)
        => Assert.Equal(expected, ReleaseTarget.AssetName(os, arch));

    [Theory]
    [InlineData("freebsd", "x64")]
    [InlineData("linux", "mips64")]
    [InlineData("", "")]
    public void An_unpublished_combination_gets_no_name(string os, string arch)
        => Assert.Null(ReleaseTarget.AssetName(os, arch));

    [Fact]
    public void This_machine_resolves_to_something()
        => Assert.NotNull(ReleaseTarget.Current());
}

public class UpdateDecisionTests
{
    [Theory]
    [InlineData("0.3.0", "v0.4.0", UpdateVerdict.Available)]
    [InlineData("0.3.0", "v0.3.1", UpdateVerdict.Available)]
    [InlineData("0.3.0", "v1.0.0", UpdateVerdict.Available)]
    [InlineData("0.3.0", "v0.3.0", UpdateVerdict.UpToDate)]
    [InlineData("0.4.0", "v0.3.0", UpdateVerdict.Ahead)]
    public void Versions_compare_as_versions_and_not_as_strings(string current, string tag, UpdateVerdict expected)
    {
        // '0.10.0' sorts before '0.9.0' as text. A tool that offered a downgrade
        // on the tenth release would be one nobody trusts to update itself.
        Assert.Equal(expected, UpdateDecision.Between(current, tag).Verdict);
    }

    [Fact]
    public void Version_ten_is_newer_than_version_nine()
        => Assert.Equal(UpdateVerdict.Available, UpdateDecision.Between("0.9.0", "v0.10.0").Verdict);

    [Fact]
    public void Build_metadata_does_not_make_a_version_different()
    {
        // 'weft --version' prints '0.3.0+<sha>'. Comparing that as text would
        // offer an update to the release already running, every single time.
        Assert.Equal(UpdateVerdict.UpToDate,
            UpdateDecision.Between("0.3.0+b635b2c4ecfb6b52", "v0.3.0").Verdict);
    }

    [Theory]
    [InlineData("not-a-version", "v0.3.0")]
    [InlineData("0.3.0", "nightly")]
    [InlineData("", "")]
    public void An_unreadable_version_refuses_to_decide(string current, string tag)
    {
        // Treating it as older would push an update on somebody running a build
        // they made themselves.
        Assert.Equal(UpdateVerdict.Unknown, UpdateDecision.Between(current, tag).Verdict);
    }

    [Fact]
    public void The_description_names_both_versions()
    {
        var d = UpdateDecision.Between("0.3.0", "v0.4.0");
        Assert.Contains("0.3.0", d.Describe(), StringComparison.Ordinal);
        Assert.Contains("0.4.0", d.Describe(), StringComparison.Ordinal);
    }
}

public class ChecksumsTests
{
    private const string Sums = """
        ded798544898654e737bcdb8f2ae24c15e69409b352fc75df7c904c2666a8226  weft-linux-arm64
        6925988dce9db91faafe113264d72a90e1defc3dcc877a3eaff4935991ca279e  weft-linux-x64
        1c590b50eae118a7b953f2252332672b6a69a0948fde254d6cbb67c1163eddc0  weft-macos-arm64
        4cd2dd0ce0d79e041dfb1ad0421e502f682f04cb9f01b7ca35ce654e75a64a20  weft-windows-x64.exe
        """;

    [Fact]
    public void The_digest_for_an_asset_is_found()
        => Assert.Equal("1c590b50eae118a7b953f2252332672b6a69a0948fde254d6cbb67c1163eddc0",
            Checksums.For(Sums, "weft-macos-arm64"));

    [Fact]
    public void A_windows_name_with_its_extension_is_found()
        => Assert.NotNull(Checksums.For(Sums, "weft-windows-x64.exe"));

    [Fact]
    public void An_asset_that_is_not_listed_gets_nothing()
        => Assert.Null(Checksums.For(Sums, "weft-freebsd-x64"));

    [Fact]
    public void A_name_that_merely_starts_the_same_is_not_a_match()
    {
        // 'weft-linux-x64' is a prefix of a future 'weft-linux-x64-musl'. Matching
        // on a prefix would verify one download against another's digest and
        // report success.
        Assert.Null(Checksums.For(Sums, "weft-linux-x6"));
        Assert.Null(Checksums.For("abc  weft-linux-x64-musl", "weft-linux-x64"));
    }

    [Fact]
    public void The_binary_mode_marker_is_understood()
    {
        // sha256sum writes ' *name' in binary mode. Windows tooling produces it,
        // and a parser that only knows the text form silently verifies nothing.
        Assert.Equal("6925988dce9db91faafe113264d72a90e1defc3dcc877a3eaff4935991ca279e",
            Checksums.For("6925988dce9db91faafe113264d72a90e1defc3dcc877a3eaff4935991ca279e *weft-linux-x64",
                          "weft-linux-x64"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("tooshort  weft-linux-x64")]
    [InlineData("   \n\n  ")]
    public void Rubbish_yields_nothing_rather_than_a_wrong_digest(string text)
        => Assert.Null(Checksums.For(text, "weft-linux-x64"));
}

public sealed class BinarySwapTests : IDisposable
{
    private readonly string _dir = TempTree.Create("weft-swap");

    public void Dispose() => TempTree.Remove(_dir);

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void The_new_binary_takes_the_old_ones_place()
    {
        var target = Write("weft", "old");
        var staged = Write("weft.staged", "new");

        BinarySwap.Replace(target, staged);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void The_replacement_is_executable()
    {
        if (OperatingSystem.IsWindows()) return;

        // A file fetched over HTTP arrives without the bit, whatever it had on the
        // machine that built it. Without this the update succeeds and the next
        // invocation is 'permission denied'.
        var target = Write("weft", "old");
        var staged = Write("weft.staged", "new");

        BinarySwap.Replace(target, staged);

        Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void A_missing_target_is_refused_with_a_reason()
    {
        var obstacle = BinarySwap.CanReplace(Path.Combine(_dir, "never-existed"));
        Assert.NotNull(obstacle);
        Assert.NotNull(obstacle.Remedy);
    }

    [Fact]
    public void Running_through_the_dotnet_host_is_refused()
    {
        // 'dotnet run' means the executable is the SDK host. Replacing it would be
        // replacing the user's .NET installation.
        var host = Write(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", "host");
        var obstacle = BinarySwap.CanReplace(host);

        Assert.NotNull(obstacle);
        Assert.Contains(".NET host", obstacle.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_writable_target_raises_no_obstacle()
        => Assert.Null(BinarySwap.CanReplace(Write("weft", "binary")));

    [Fact]
    public void The_write_probe_leaves_nothing_behind()
    {
        var target = Write("weft", "binary");
        BinarySwap.CanReplace(target);

        Assert.Equal(["weft"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_binary_left_aside_by_an_earlier_update_is_cleaned_up()
    {
        // Windows cannot delete a running executable, so the previous one is
        // removed by the process that starts after it.
        var target = Write("weft", "current");
        File.WriteAllText(target + BinarySwap.OldSuffix, "previous");

        Assert.True(BinarySwap.CleanupLeftovers(target));
        Assert.False(File.Exists(target + BinarySwap.OldSuffix));
    }

    [Fact]
    public void Cleaning_up_when_there_is_nothing_to_clean_says_so()
        => Assert.False(BinarySwap.CleanupLeftovers(Write("weft", "current")));
}
