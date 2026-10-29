# Docker package readiness — October 28, 2026

**NO-GO for publishing the exact `ghcr.io/gaboy498-byte/sonarr-pro:v2026.10` package with the workflow unchanged.** This is an investigation and proposed follow-up, not authorization to dispatch the workflow. The [GitHub release](https://github.com/gaboy498-byte/Sonarr-Pro/releases/tag/v2026.10) remains source-only.

## Audited workflow behavior

[docker.yml](../.github/workflows/docker.yml) listens for main/tag pushes and manual dispatch, but its publishing job requires `github.event_name == 'workflow_dispatch'`. Pushes can create skipped workflow records; they do not build, smoke-test or publish through this job. The guard must remain.

[ci.yml](../.github/workflows/ci.yml) independently runs backend/frontend CI and a **non-publishing image build on pull requests**. That job has no runtime smoke test. No other workflow files are tracked. A PR is therefore not a way to avoid image builds merely by marking it draft. This documentation-only task uses GitHub's documented commit-level skip marker to honor its no-image-build restriction, with local documentation checks reported separately; no workflow configuration is changed.

| Area | Confirmed implementation | Remaining gate |
| --- | --- | --- |
| Exact image tag | Metadata derives semantic-version tags plus `latest` for tag refs and `edge` for main. | `v2026.10` is a two-component calendar tag, not valid SemVer. The semver rules do not emit that exact tag; there is no raw/ref rule for it. Add an explicitly reviewed calendar-tag rule and test metadata without building/pushing. Do not dispatch on main expecting the release tag: that path selects `edge`. |
| Source provenance | Checkout uses the chosen dispatch ref; final publication applies metadata labels. | Require the intended source commit, record resolved base digests and image digests, and verify labels on every architecture. There is no explicit signed provenance-attestation policy in this workflow. The root Dockerfile's inherited source label points upstream; the final metadata-action labels should override it, but the smoke-test/local default does not. |
| Permissions | Publishing job has `contents: read` and `packages: write`; login uses `GITHUB_TOKEN`. Checkout does not persist credentials. | Confirm package-to-repository permissions for the first publish. PR CI has no package-write grant or registry login. |
| Package visibility | No package was published by v2026.10. | The available API credential lacks `read:packages`; package inventory/visibility could not be independently inspected. GitHub creates new packages private by default. Verify correct repository linking, visibility and anonymous pull after a separately authorized first publication before advertising availability. |
| ImageSharp | The approved public assembly-scoped grant for `Sonarr.Core` is hash-checked, mounted read-only and validated officially in Release/net10.0. | Track its documented January 10, 2028 expiry and scope. Do not generalize this grant to other assemblies or substitute a private key. Review component license and binary-redistribution obligations; build authorization alone is not a blanket redistribution clearance. |
| MonoTorrent | Dependency commit, archive hash, identity, contents and MIT notice are checked; the notice is included in the image. | Complete hardened source/build recipe remains unavailable. The archive can be retrieved and verified, but exact source reproduction is not established. Preserve the pinned archive; do not substitute or repack it. |
| Runtime and platforms | Docker publishes self-contained .NET; release validation recorded 10.0.13 on Linux/amd64. Tag dispatch selects amd64 and arm64; only amd64 gets a startup smoke test. | Floating SDK/runtime-deps tags and OS feeds can change. Record actual runtime/security dependency versions, rerun audits including OS/npm as appropriate, and validate arm64 runtime behavior before claiming equal support. |
| Secret exclusion | Controlled tracked-file context, isolated feed/cache, package checksums and exact public-license allowlist are implemented. No private license secret is needed. | Reverify the context/final image for a new build. Registry credentials stay in the workflow login step; do not add them as build arguments. Untrusted PRs must not receive private secrets or package-write rights. |

## Smallest proposed publishing follow-up

1. Review a separate change to `.github/workflows/docker.yml` that supports the exact calendar tag and verifies the intended release commit. Keep publishing manual-only. Do not move or recreate the existing source tag to include workflow changes; deliberately design how a later reviewed workflow builds the immutable release source.
2. Resolve the recorded license/redistribution questions for the combined binary distribution, retain notices and identify corresponding application source. The MonoTorrent MIT archive's known source-provenance limitation must remain disclosed.
3. Test tag/label derivation without registry publication. Decide whether both architectures are required and validate each claimed runtime. Record base-image digests, runtime/dependency versions and audit results; pin inputs or clearly document why bit-for-bit reproduction is not claimed.
4. In a separately authorized publication, use repository-scoped `GITHUB_TOKEN` permissions, publish the intended tag, verify package association/visibility and perform anonymous digest-pinned pulls and isolated checks. Do not announce a public image until these checks pass.

No workflow, Dockerfile, Compose, dependency, tag or release object is changed by this documentation PR. No image build, push or manual dispatch is performed as part of it.

## Supporting references

- [Metadata action v5: semantic-version handling](https://github.com/docker/metadata-action/blob/v5/src/meta.ts) and [tag configuration](https://github.com/docker/metadata-action/tree/v5#typesemver).
- [GitHub Container registry permissions and visibility](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
- [GitHub commit-level workflow skipping](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/skip-workflow-runs).
- [Official Six Labors license guidance](https://docs.sixlabors.com/articles/imagesharp/index.html) and [this project's approved grant documentation](../licenses/SixLabors/README.md).
- [Hardened MonoTorrent archive provenance](https://github.com/gaboy498-byte/Sonarr-Pro-Dependencies/blob/53a58c56d3bf847b97f4b577d9b575c642369188/PROVENANCE.md).
