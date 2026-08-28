using System.Security.Cryptography;
using Weft.Core.Store;

namespace Weft.Core.Tests.Store;

/// <summary>
/// Pins the gear table.
/// </summary>
/// <remarks>
/// This is a format guard, not a unit test. Every chunk boundary in every store
/// ever written depends on these 256 values. If one changes, nothing already
/// stored is ever found by content again and the store silently re-uploads
/// itself, with no error anywhere to say why.
///
/// The expected values were computed by an independent implementation, so this
/// checks the derivation against something other than itself.
/// </remarks>
public class GearTests
{
    [Fact]
    public void The_table_matches_an_independently_computed_reference()
    {
        Assert.Equal(0xB612E2D3788499ECUL, Gear.Table[0]);
        Assert.Equal(0xA59986536412453FUL, Gear.Table[1]);
        Assert.Equal(0xCD8D143D578C3D42UL, Gear.Table[2]);
        Assert.Equal(0x8F253B60676B91EDUL, Gear.Table[3]);
        Assert.Equal(0x2D8F392994359243UL, Gear.Table[255]);
    }

    [Fact]
    public void The_whole_table_has_a_fixed_fingerprint()
    {
        // Catches a change anywhere in the 251 entries the test above does not
        // name, which is where a subtle corruption would otherwise hide.
        var bytes = new byte[256 * 8];
        for (var i = 0; i < 256; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 8, 8), Gear.Table[i]);

        Assert.Equal(
            "B19AC25BF1A77880331AC904DECE65E6CF0CB0497C74EEF225F12E269B5EA664",
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void The_table_has_256_entries_and_no_duplicates()
    {
        Assert.Equal(256, Gear.Table.Length);

        // A repeated value makes two different bytes indistinguishable to the
        // boundary function, which biases where cuts land.
        Assert.Equal(256, Gear.Table.Distinct().Count());
    }

    [Fact]
    public void No_entry_is_zero()
    {
        // A zero entry contributes nothing to the rolling value, so a run of that
        // byte would stop moving the fingerprint and boundaries would cluster.
        Assert.DoesNotContain(0UL, Gear.Table);
    }
}
