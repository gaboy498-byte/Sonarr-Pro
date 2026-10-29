# Security policy for this custom Sonarr-Pro fork

This policy applies to [gaboy498-byte/Sonarr-Pro](https://github.com/gaboy498-byte/Sonarr-Pro), not official Sonarr or another fork. Security hardening reduces specific risks; it is not a guarantee that downloads or the application are free of vulnerabilities.

## Reporting a suspected vulnerability

Do not publish API keys, credential-bearing URLs, private license material, configuration files, databases, media paths identifying other users, or an actionable exploit in a public issue.

As of October 28, 2026, GitHub private vulnerability reporting is **not enabled** for this repository, and no dedicated private security contact is documented. If GitHub later offers a **Report a vulnerability** button in this repository's Security tab, use that private channel. Otherwise, open a [minimal contact request](https://github.com/gaboy498-byte/Sonarr-Pro/issues/new) asking the maintainer to establish a private channel, without disclosing exploit details. Do not post those details until a private channel has been agreed.

For a private report, useful information includes the affected source commit, platform, relevant setting, impact and a minimal reproduction. Sanitize logs and avoid attaching a complete configuration backup. There is no guaranteed response time, support window or backport commitment.

Do not direct custom-fork vulnerabilities to the official Sonarr maintainers by default. If a problem is independently confirmed in official Sonarr, follow that project's current security policy for the shared issue.

## Dependency and release scope

- v2026.10 is a source-only release. A local validated image is not a published GHCR package.
- Keep dependency auditing and the official Six Labors build validator enabled. A prior clean audit does not cover future advisories, npm dependencies or container OS packages.
- Use only the pinned hardened MonoTorrent archive and its checksum verification. Its patched source/build recipe is not currently reproducible; see [provenance](https://github.com/gaboy498-byte/Sonarr-Pro-Dependencies/blob/53a58c56d3bf847b97f4b577d9b575c642369188/PROVENANCE.md).
- The specifically approved public Six Labors assembly-scoped build grant is documented in [licenses/SixLabors](licenses/SixLabors/README.md). Its public status does not extend to unrestricted Community or commercial license files.

Operational installation guidance is in [docs/INSTALLATION.md](docs/INSTALLATION.md). Fork-specific bug reports belong [here](https://github.com/gaboy498-byte/Sonarr-Pro/issues).
