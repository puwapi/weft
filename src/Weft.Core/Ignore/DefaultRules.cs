namespace Weft.Core.Ignore;

/// <summary>
/// The rules a fresh <c>weft init</c> writes out.
/// </summary>
/// <remarks>
/// These govern <b>loose files only</b>, that is, files outside every git
/// checkout. Anything inside a repository is decided by git itself, which
/// already honours that repository's .gitignore.
///
/// That distinction removes a whole class of mistake. A blanket 'bin' or 'dist'
/// rule looks obviously right and is wrong: on the reference tree,
/// 'infra/server/dev/bin' holds 25 tracked shell scripts and 'knowledge/mcp/dist'
/// holds 2 tracked files. Because those paths come from git and never from the
/// walk, weft cannot lose them however aggressive these defaults are.
/// </remarks>
public static class DefaultRules
{
    /// <summary>Regenerable output. Overridable.</summary>
    public const string Ignore = """
        # weft ignore: things that regenerate.
        # These apply to loose files only. Files inside a git repository are
        # decided by that repository's own .gitignore, which weft asks git about.
        # Overridable: see .weftnever for what is not.

        # Dependency trees and build output
        node_modules
        .next
        .turbo
        dist
        out
        bin
        obj
        target
        .venv
        __pycache__
        coverage
        TestResults
        Pods

        # Unity, by far the largest single contributor on a game project
        Library/
        Temp/
        Logs/
        UserSettings/

        # Generated bundles that have a source of truth elsewhere.
        # Syncing a generated artefact creates a second version of the truth,
        # which then disagrees with the first.
        ds-bundle
        _exports

        # Other synchronisers' bookkeeping. Left behind, these make two tools
        # fight over the same tree.
        .stfolder
        .stversions
        *.sync-conflict-*

        # OS and editor noise
        .DS_Store
        Thumbs.db
        desktop.ini
        *.swp
        *~
        .idea
        **/.vscode/chrome-debug-profile

        # Large regenerable archives
        *.tgz.tmp
        *.tmp
        """;

    /// <summary>
    /// Confidential. Not overridable, not by --force.
    /// </summary>
    /// <remarks>
    /// Everything snapshotted reaches the remote, so a mistake here does not stay
    /// on one machine. Negation is rejected in this file; '=name' exempts one
    /// exact literal name and cannot widen to a class.
    /// </remarks>
    public const string Never = """
        # weft never: confidential. These are refused, and no flag overrides them.
        # Negation ('!') is a parse error here on purpose.
        # '=name' exempts one exact, literal name. No wildcards allowed.

        # Private key material
        *.pem
        *.key
        *.p12
        *.pfx
        *.jks
        *.keystore
        *.cer
        *.crt
        *.der
        id_rsa
        id_dsa
        id_ecdsa
        id_ed25519
        *.certSigningRequest

        # Environment files. '.env.example' documents variable names and carries
        # no secret, so it is exempted by exact name rather than by a pattern.
        .env
        .env.*
        =.env.example
        =.env.sample
        =.env.template

        # Cloud and service credentials.
        # Note the leading '**/': a pattern containing a '/' is anchored to the
        # root, so '.aws/credentials' would only ever match at the top level.
        **/.aws/credentials
        .npmrc
        .pypirc
        service-account*.json
        *.secret
        secrets.*

        # Anything beyond these universal classes belongs in YOUR .weftnever, not
        # in the tool's defaults. weft cannot know which of your documents are
        # sensitive, and a default list that tries to guess grows without bound
        # while still missing the case that matters. The secret scanner is the
        # backstop for content that lands somewhere unexpected.
        """;
}
