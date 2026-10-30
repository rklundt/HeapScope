# Security policy

HeapScope reads memory dumps, which hold everything a process had in memory: passwords, tokens, connection strings,
personal data. Its design keeps them on your machine (`docs/ARCHITECTURE.md` section 12). If you find a way around that,
or any other vulnerability, please report it privately.

## Reporting

- Use GitHub's private vulnerability reporting: the **Report a vulnerability** button on the repository's **Security**
  tab, or
- write to rayklundt (at) outlook (dot) com.

Please do not open a public issue for a vulnerability, and never attach a real memory dump, gcdump, trace or index cache:
describe how to make one instead (the fixtures in `fixtures/` are safe to use).

You can expect an answer within a week. Fixes go into the next release; the release notes (`RELEASE-NOTES.md`) name
security fixes.

## Supported versions

HeapScope is in pre-release. Only the latest release receives fixes.

## Verifying a release

Each GitHub release carries `SHA256SUMS` for its builds, an SBOM (CycloneDX) per build, and a build provenance
attestation, which `gh attestation verify <file> --repo rklundt/HeapScope` checks.
