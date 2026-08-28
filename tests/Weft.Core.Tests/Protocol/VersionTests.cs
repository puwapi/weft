using System.Reflection;
using Weft.Core.Protocol;

namespace Weft.Core.Tests.Protocol;

public class VersionTests
{
    [Fact]
    public void The_version_a_person_sees_is_the_version_the_server_judges()
    {
        // The binary prints its assembly version; the server gates writes on the
        // Weft-Client header, which carries WeftVersion.Build. When those drifted
        // apart, the binary said 1.0.0 while the floor was 0.2.0, so the number on
        // screen was not the number being refused.
        var assembly = typeof(WeftVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion
            .Split('+')[0];

        Assert.Equal(WeftVersion.Build, assembly);
    }

    [Fact]
    public void The_build_version_is_something_Version_can_parse()
    {
        // The server compares it with Version.TryParse. A build calling itself
        // '0.3.0-beta' would fall below every floor and be refused every write,
        // with a message about being too old.
        Assert.True(Version.TryParse(WeftVersion.Build, out _),
            $"'{WeftVersion.Build}' is not a version the server's comparison understands");
    }

    [Fact]
    public void The_protocol_version_is_separate_from_the_build_version()
    {
        // Deliberately unrelated numbers. Conflating them turns every release into
        // a forced upgrade on every machine, for a wire format that did not change.
        Assert.Equal(1, WeftVersion.Protocol);
    }
}
