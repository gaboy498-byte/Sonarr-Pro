# Building the v2026.10 source

These instructions describe the committed CI/Docker build paths. They were checked against source for this documentation change, **not executed as a new build**. Existing release validation is linked in the [README](../README.md#recorded-validation-and-limitations).

## Requirements

- Git checkout, Bash, Python 3, curl, `sha256sum` and standard Unix tools. The examples use a Linux/WSL Bash environment; Git Bash with Windows .NET was used for dependency preparation during development, but path handling differs.
- Backend on the host: a compatible .NET 10 SDK. [global.json](../global.json) requests 10.0.402. The recorded Docker build used 10.0.403 within the same feature band under the default patch roll-forward policy. Run `dotnet --version` **from the checkout** to see the selected SDK.
- Frontend on the host: Node.js 20 and Yarn Classic, matching current CI. This is the release's toolchain, not a claim that every listed major remains vendor-supported.
- Docker path: BuildKit-capable Docker with Linux containers. Host .NET, Node and Yarn are not needed for the image build; the host provisioning tools still are. Only Linux/amd64 runtime validation is recorded.

Use the release tag for the released source:

```bash
git clone https://github.com/gaboy498-byte/Sonarr-Pro.git
cd Sonarr-Pro
git checkout --detach v2026.10
```

## Prepare the hardened dependency

Run the following in the checkout. Keep this shell open for subsequent steps. Use a new temporary directory rather than an existing deployment/configuration directory.

```bash
export RUNNER_TEMP="$(mktemp -d)"
export GITHUB_ENV="$RUNNER_TEMP/provision.env"
bash .github/scripts/prepare-monotorrent.sh
set -a
. "$GITHUB_ENV"
set +a
```

The helper downloads the exact archived MonoTorrent **3.0.2-sonarrsecurity.1.rev0002** from dependency commit `53a58c56d3bf847b97f4b577d9b575c642369188`. It verifies package identity, contents, MIT notice and SHA-256:

```text
039eda4b39cb2d17c3e187ada0f5c4cd12852ec51c8ea968904024217aa2fcfa
```

Only after verification does it export `MONOTORRENT_NUGET_CONFIG` and an isolated `NUGET_PACKAGES` cache through `GITHUB_ENV`. The generated configuration maps MonoTorrent to that feed and retains approved feeds/audit sources. Do not use a standard MonoTorrent package or disable auditing when restore fails. Original patched-source reproduction remains unresolved; see [provenance](https://github.com/gaboy498-byte/Sonarr-Pro-Dependencies/blob/53a58c56d3bf847b97f4b577d9b575c642369188/PROVENANCE.md).

## Host backend and frontend

After dependency preparation, restore and validate the official license before compiling:

```bash
dotnet --version
dotnet restore src/Sonarr.sln -p:Configuration=Debug \
  --configfile "$MONOTORRENT_NUGET_CONFIG" --force --no-http-cache \
  -p:NuGetAudit=true -p:NuGetAuditMode=all -p:NuGetAuditLevel=low
dotnet msbuild src/NzbDrone.Core/Sonarr.Core.csproj -nologo \
  -t:SixLabors_ValidateLicense -p:Configuration=Release -p:TargetFramework=net10.0
dotnet build src/Sonarr.sln -c Debug --no-restore
dotnet test src/NzbDrone.Core.Test/Sonarr.Core.Test.csproj \
  -c Debug --no-build --no-restore
yarn install --frozen-lockfile --network-timeout 600000
yarn lint
yarn stylelint
yarn build --env production
```

This mirrors the host CI's Debug path. For a host Release build, use `Configuration=Release` at restore and `-c Release` for build/tests. For Linux self-contained publication use the Docker path below, which also supplies the Posix platform and runtime identifier.

Build the **solution**, not individual projects: StyleCop configuration depends on `SolutionDir`. The standalone project invocation above runs only the official licensing target. The frontend requires `--env production` for a distributable bundle; a development bundle is not a deployment artifact.

Sonarr.Core directly references ImageSharp **4.1.3** and supplies the included public Community grant through `SixLaborsLicenseFile`. The approved assembly name is exactly `Sonarr.Core`. Do not rename it, edit the issued license or bypass validation. See [license scope](../licenses/SixLabors/README.md) and [official vendor guidance](https://docs.sixlabors.com/articles/imagesharp/index.html). No private license secret is required for the approved assembly.

## Build a local image

After the dependency preparation above, stage the controlled context and build it locally:

```bash
export GITHUB_OUTPUT="$RUNNER_TEMP/docker-context.env"
python3 .github/scripts/prepare-docker-context.py
context="$(sed -n 's/^context=//p' "$GITHUB_OUTPUT")"
test -n "$context"
docker build --platform linux/amd64 \
  --label org.opencontainers.image.source=https://github.com/gaboy498-byte/Sonarr-Pro \
  --label "org.opencontainers.image.revision=$(git rev-parse HEAD)" \
  --tag sonarr-pro-local:v2026.10 "$context"
```

The context helper copies tracked build inputs and the hash-approved public license; it excludes unrelated local files. It stages the verified MonoTorrent package and configuration for read-only BuildKit mounts. The Dockerfile restores with auditing, runs official licensing validation, publishes a self-contained backend and builds the production UI. Build-time dependency/license mounts are not copied wholesale into the final image; MonoTorrent's MIT notice is copied explicitly.

The explicit source label avoids the root Dockerfile's inherited upstream source-label default. The final runtime uses `/app/Sonarr`, persistent `/config` and default port 8989. [INSTALLATION.md](INSTALLATION.md) explains mounts and port mapping. Building an image is not validation of its startup, integrations or migration behavior. Isolated testing is required before any deployment.

The base tags and OS package feeds are floating. `RuntimeFrameworkVersion` records 10.0.13 with `TargetLatestRuntimePatch=true`; a new restore/build must verify its actual resolved/runtime versions. A later image need not be byte-identical to the previously validated one. Do not claim a new build is the original release image merely because it uses the source tag. Review [publication limitations](DOCKER-PUBLISHING.md) before redistribution.
