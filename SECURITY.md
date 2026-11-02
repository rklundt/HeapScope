# Security policy

HeapScope reads memory dumps, which hold everything a process had in memory: passwords, tokens, connection strings,
personal data. Its design keeps them on your machine ([docs/architecture/security-privacy.md](docs/architecture/security-privacy.md)). If you find a way around that,
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

Releases after 3.5.0 carry their builds (3.8.0: one tarball per runtime, win-x64, linux-x64, osx-arm64 and osx-x64;
from 3.9.0: a win-x64 `.zip`, a linux-x64 `.tar.gz` and the CLI's dotnet tool package, see INSTALL.md), an SBOM
(CycloneDX) for the app, the command line tool and the worker, and `SHA256SUMS` for them all. Releases 2.2.0 and 3.5.0
are source only. Each file has a build provenance attestation in GitHub's attestation store, which
`gh attestation verify <file> --repo rklundt/HeapScope` checks.
