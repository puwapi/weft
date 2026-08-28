using Weft.Core.Ignore;

namespace Weft.Core.Tests.Ignore;

/// <summary>
/// Guards on the shipped rule text itself, not on the matcher.
/// </summary>
/// <remarks>
/// Every rule here is a decision that reaches every user of the tool. These
/// tests exist because the failure mode is silent: a mis-anchored pattern parses,
/// matches nothing, and looks exactly like a rule that works.
/// </remarks>
public class DefaultRulesTests
{
    private static IEnumerable<string> Rules(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#' && l[0] != '=');

    [Fact]
    public void A_multi_segment_rule_is_explicitly_anchored_or_explicitly_not()
    {
        // gitignore anchors any pattern containing a '/'. '.aws/credentials'
        // therefore matches only at the top level, which is almost never what the
        // author of such a rule means. Requiring a leading '**/' or a leading '/'
        // forces the choice to be visible in the rule text.
        var offenders = Rules(DefaultRules.Ignore).Concat(Rules(DefaultRules.Never))
            .Where(r => r.TrimEnd('/').Contains('/'))
            .Where(r => !r.StartsWith("**/", StringComparison.Ordinal)
                     && !r.StartsWith('/'))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These rules contain a '/' and are therefore anchored to the root, which is " +
            "probably not intended. Prefix with '**/' to match at any depth, or with '/' " +
            "to state that the anchoring is deliberate:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_shipped_rule_parses()
    {
        foreach (var r in Rules(DefaultRules.Ignore).Concat(Rules(DefaultRules.Never)))
            Assert.True(Glob.Parse(r) is not null, $"rule does not parse: '{r}'");
    }

    [Fact]
    public void The_shipped_sets_load_together()
    {
        // Catches a negation accidentally added to the never set, which is a
        // parse error by design and would otherwise only surface at 'weft init'.
        var ex = Record.Exception(() => IgnorePolicy.Parse(DefaultRules.Ignore, DefaultRules.Never));
        Assert.Null(ex);
    }

    [Fact]
    public void The_never_set_carries_no_negation()
        => Assert.DoesNotContain(DefaultRules.Never.Split('\n').Select(l => l.Trim()),
                                 l => l.StartsWith('!'));
}
