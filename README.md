# Sonarr-Pro — Matt's fork

An independent, self-hosted television library manager built on [Sonarr](https://github.com/Sonarr/Sonarr) and [upstream Sonarr-Pro](https://github.com/KakarottoCake/Sonarr-Pro). This fork extends their metadata and download-management features with security hardening, queue reliability corrections, and multi-season import improvements.

**This is not official Sonarr.** It is not affiliated with or supported by the Sonarr project. Report this fork's issues [in this repository](https://github.com/gaboy498-byte/Sonarr-Pro/issues), rather than asking upstream volunteers to support custom code.

**Current release: [v2026.10](https://github.com/gaboy498-byte/Sonarr-Pro/releases/tag/v2026.10)** · [Installation](docs/INSTALLATION.md) · [Building](docs/BUILDING.md) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

The release is **source-only**: there are no attached application binaries or verified public GHCR images for this fork. The upstream image is a different distribution and does not install these custom changes. See [Docker publishing readiness](docs/DOCKER-PUBLISHING.md) before considering a registry publication.

## What's in v2026.10

| Area | Improvements in this custom fork |
| --- | --- |
| Release security | Hardened fake-release submission routes and suspicious executable/script title checks; magnet metadata inspection and HTTP redirect-to-magnet protection. |
| Queue reliability | Refresh pending-release state after removal, guard overlapping selections, honor download-client removal options, and preserve useful errors and retry selections. |
| Multi-season handling | Explicit season-range parsing, episode mapping, safer automatic imports, legacy grab-membership reconciliation, and size-decision regression coverage. |
| Import visibility | Per-episode import progress reporting. |
| Release Profile interface | Show **`(Any)`** when the Indexer selection is empty, retaining search, checkboxes and multi-select behavior. |
| Dependencies | ImageSharp **4.1.3**, hardened MonoTorrent **3.0.2-sonarrsecurity.1.rev0002**, and .NET runtime/security dependency corrections validated at **10.0.13**. |

`(Any)` is only a display label. The API still stores `indexerIds: []`, meaning all indexers, including newly added ones, subject to the profile's other settings. Existing empty profiles need no edit or resave.

## Feature origins

**Official Sonarr** provides the core library management, monitoring, quality selection, download-client integration and Release Profiles. Must Contain / Must Not Contain restrictions are inherited. Reject Unaired Releases, Air Date Grace Period and season-pack air-date controls are inherited from official Sonarr **v5 development**; this does not imply availability in every older Sonarr release.

**Upstream Sonarr-Pro** adds metadata sources including TMDB, AniList and MyAnimeList/Jikan, selectable episode orderings, absolute-number matching, initial multi-season support, optional unmonitored pack trimming, anime-aware matching and initial torrent-content filtering. These are not original work of this custom fork. [FORK.md](FORK.md) preserves upstream's design notes; its first-person statements and original defaults describe that inherited work, not a maintenance promise from this fork.

**This fork** extends that foundation with the changes in the table above. September history is preserved in the [September source baseline](https://github.com/gaboy498-byte/Sonarr-Pro/tree/sonarr-pro-matt-2026.09); the October release adds dependency provisioning, licensing integration, security dependency corrections and queue fixes. Upstream synchronization is a separate, reviewed activity, not an automatic update policy.

## Security scope

Release Profile title restrictions and torrent-content inspection serve different purposes. A Must Not Contain restriction such as `.exe` checks the **release title**, not every downloaded file's contents. Torrent inspection depends on available metadata and applicable settings. Direct/fallback magnet compatibility exceptions remain; strict redirect inspection does not mean every magnet is rejected when metadata is unavailable.

Explicit interactive grabs may proceed despite Release Profile rejection decisions. Applicable torrent-submission security checks still operate on those routes. These protections are not complete malware protection and do not establish that a download is safe. Usenet client support is inherited; torrent-content inspection does not inspect NZB payloads, and custom pack behavior has primarily been tested with torrents.

The hardened MonoTorrent package is pinned to an exact archive and SHA-256. Its complete patched source/build recipe has not been recovered, so **verified provisioning is not source reproducibility**. See the [dependency provenance](https://github.com/gaboy498-byte/Sonarr-Pro-Dependencies/blob/53a58c56d3bf847b97f4b577d9b575c642369188/PROVENANCE.md). Do not substitute standard MonoTorrent.

ImageSharp's approved, assembly-scoped Community build license is included for **`Sonarr.Core`**, with exact capitalization. It is a build input, not a private deployment secret. Its scope and renewal requirements are documented in [licenses/SixLabors](licenses/SixLabors/README.md); other dependencies retain their own licensing terms.

## Installation and compatibility

Start with [installation guidance](docs/INSTALLATION.md) and the [source-build instructions](docs/BUILDING.md). Building a local Docker image requires the verified dependency/context preparation; a bare `docker build .` is not the supported path for this fork.

- Backend: .NET 10; `global.json` requests SDK **10.0.402**. The validated Linux build selected **10.0.403** under the existing patch roll-forward policy. Runtime and SDK versions are separate.
- Frontend build: Node.js **20**, Yarn Classic and the committed lockfile, matching the current CI/Docker configuration. This records the validated toolchain, not a promise about its ongoing vendor support.
- Containers: Linux/amd64 has recorded build, isolated-runtime and deployment validation. The workflow declares arm64 release builds, but equivalent runtime validation is not established here.
- New container defaults: application port **8989**, persistent `/config`, and `PUID`/`PGID` **1000**. Existing configuration may override the listening port. Mount downloads and media with consistent paths and appropriate permissions.

The root [docker-compose.yml](docker-compose.yml) still references **upstream's** image. It is retained as inherited configuration, not a quick start for this custom release. No published `latest`, `edge` or `v2026.10` image is promised for this fork.

## Recorded validation and limitations

Release source: [`80a719a169ba2251d8a5d7a27106ce1dc2274ae7`](https://github.com/gaboy498-byte/Sonarr-Pro/commit/80a719a169ba2251d8a5d7a27106ce1dc2274ae7).

| Check | Recorded result for the release |
| --- | --- |
| [GitHub CI](https://github.com/gaboy498-byte/Sonarr-Pro/actions/runs/40403507415) | Core: **5,425 passed, 0 failed, 28 skipped**; frontend lint and production build passed. |
| Focused Windows Release regressions | **187 passed**; Windows Release solution build passed. |
| Frontend tests | **48 passed**. |
| Docker validation | Local Linux/amd64 build, isolated startup, runtime/dependency checks and rendered-browser checks passed; controlled deployment completed October 27, 2026. |

These are recorded results, not a new test run for every README revision. Queue success/failure cases used isolated fixtures; **real production torrent deletion was not tested**. Broader ImageSharp format, concurrency and resource-stress coverage is incomplete. Past NuGet audit results do not guarantee the absence of later advisories or cover npm/OS packages. This documentation update does not claim a new vulnerability audit.

Support is best-effort, with no guaranteed response time or monthly upstream-merge schedule. Include the source commit, platform and sanitized reproduction details in [bug reports](https://github.com/gaboy498-byte/Sonarr-Pro/issues). Do not attach configuration databases, license material, API keys or credential-bearing URLs. See [SECURITY.md](SECURITY.md) before reporting a vulnerability.

## Acknowledgments and licensing

Credit for official Sonarr belongs to the Sonarr contributors. Credit for upstream Sonarr-Pro's metadata, ordering, pack and initial filtering features belongs to KakarottoCake and upstream contributors. This repository preserves their history, [copyright notices](COPYRIGHT.md) and [GPL license](LICENSE.md). No ownership of inherited features is claimed.

Sonarr Pro is a derivative work of [Sonarr](https://github.com/Sonarr/Sonarr), copyright the Sonarr contributors, whose copyright notices are retained. Credit for the several hundred thousand lines this is built on belongs to them; the changes listed above are the only part that's new here.

The application uses the GPL terms in [LICENSE.md](LICENSE.md) and [COPYRIGHT.md](COPYRIGHT.md). Third-party components are not all GPL: MonoTorrent retains its MIT notice, and ImageSharp uses Six Labors' license terms. The included assembly-scoped build grant is not a blanket license for other assemblies or uses. Redistributors must review the applicable component terms and corresponding-source obligations; see [Docker publishing readiness](docs/DOCKER-PUBLISHING.md).
