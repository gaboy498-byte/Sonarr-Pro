# Contributing to Matt's Sonarr-Pro fork

Contributions are welcome, including AI-assisted ones. There's no CLA and no
template to fill in.

## Pull requests

- **Explain the reasoning, not just the change.** A PR that says what broke and
  why this is the right fix is worth several that only say what changed.
- **Say what you actually verified.** "Tests pass, ran it against a real library"
  and "compiles, untested" are both fine answers. Claiming the first while
  meaning the second is not.
- **Prefer changes that stay mergeable with upstream.** This is a fork of an
  actively developed project, and every unnecessary divergence makes pulling in
  upstream fixes harder. If a bug turns out to be upstream's rather than this
  fork's, it's usually better to report it there than to patch around it here.

AI-assisted work doesn't need to be disguised. `Co-Authored-By` trailers are
accurate attribution and are welcome. See [AGENTS.md](AGENTS.md) if you're an
agent working in this repository.

## Before opening a PR

Follow [docs/BUILDING.md](docs/BUILDING.md) first: the backend requires the
verified hardened MonoTorrent feed and the approved Six Labors build license.
A plain restore against NuGet.org cannot obtain the custom package.

```bash
dotnet build src/Sonarr.sln -c Debug --no-restore
yarn lint
yarn stylelint
yarn build --env production
```

These commands assume a matching Debug restore and frontend installation from
the build guide. CI runs an audited restore, official license validation,
backend build/Core tests, frontend lint/stylelint/production build, and a
non-publishing Linux/amd64 Docker build on pull requests. That PR image build
does not run a container smoke test. The separate publishing workflow remains
manual-only. Do not describe a skipped job as a passed test.

For documentation-only changes, validate relative links, instructions and
attribution. Do not claim application tests were rerun if they were not. If a
task explicitly prohibits image builds, agree how to handle the PR image job
before opening the PR; a draft PR still triggers it. GitHub's commit-level
`[skip ci]` option skips the entire push/PR workflow, not just Docker, and must
be disclosed. Do not disable workflow or security checks to hide failures.

Build the **solution**, not individual project files — `Directory.Build.props`
loads `stylecop.json` through `$(SolutionDir)`, which is only defined for a
solution build. Building a bare `.csproj` produces hundreds of spurious StyleCop
errors on code that is perfectly fine.

The frontend needs `yarn build --env production`. Without that flag webpack emits
eval-source-map output and the UI loads as a blank page with no error in the
console.

## Bug reports and suggestions

[Open an issue](https://github.com/gaboy498-byte/Sonarr-Pro/issues). Suggestions
are as welcome as bug reports.

Please don't raise Sonarr Pro problems on Sonarr's issue tracker, forums, or
Discord — they can't help with code they didn't write, and it costs their
volunteers time.

For bugs, include the source commit, platform, what you expected, what happened,
and relevant **sanitized** lines from System → Logs. Remove API keys,
credential-bearing URLs and personal paths; never attach configuration databases
or complete backups. Follow [SECURITY.md](SECURITY.md) for vulnerabilities.
If it involves a specific series or
release, naming it helps more than anything else; most of the interesting bugs in
this fork have been specific to one title's metadata.
