# Installation and Docker guidance

## Choose the right distribution

[v2026.10](https://github.com/gaboy498-byte/Sonarr-Pro/releases/tag/v2026.10) is a source-only release of **gaboy498-byte/Sonarr-Pro**. Its source commit is `80a719a169ba2251d8a5d7a27106ce1dc2274ae7`. There are no attached application binaries or verified public container images for this fork.

The root [docker-compose.yml](../docker-compose.yml) pulls `ghcr.io/kakarottocake/sonarr-pro:latest`: that is **upstream Sonarr-Pro**, not this custom fork. Do not use it expecting the custom security and queue changes. No `docker pull ghcr.io/gaboy498-byte/sonarr-pro:v2026.10` quick start is offered until that package has been published and verified.

Build your own image using [BUILDING.md](BUILDING.md), or wait for a separately announced package. A source ZIP is source code, not a ready-to-run installation. The provisioning scripts require a Git checkout.

## Example for an independently built local image

The following is an illustrative **new-instance** Compose definition for the local image name used in the build guide. It is not a command to replace an existing deployment. No production paths or settings are supplied.

```yaml
services:
  sonarr-pro:
    image: sonarr-pro-local:v2026.10
    restart: unless-stopped
    ports:
      - "8990:8989"
    environment:
      PUID: "1000"
      PGID: "1000"
      TZ: Etc/UTC
    volumes:
      - ./config:/config
      - /path/to/data:/data
```

Edit the example for your host before use. Port 8990 here is an arbitrary host port mapped to the image's default application port 8989. Open `http://localhost:8990` only for that mapping. Existing `/config` can specify a different internal port; match the mapping to the actual configuration. Other services' ports and networks are deployment-specific.

The image entrypoint uses `PUID`/`PGID` (default 1000/1000) to drop privileges when starting as root. Match them to an account with the required filesystem access on Linux; Windows Docker Desktop permissions differ. `/config` holds settings, database and logs and must persist. The entrypoint attempts ownership changes within `/config`, so an existing installation's permissions should be reviewed before first use.

Map the download client's completed directory and library consistently. A common parent mount can permit moves/hardlinks **when the underlying filesystem supports them**; it cannot make hardlinks work across separate filesystems. Sonarr must see the completed-download paths reported by the client, either directly or through an appropriate remote path mapping. The example does not configure a download client, indexer or library for you.

## Existing installations and rollback

Before migrating, preserve a consistent, verified configuration/database backup and the matching old image or binaries. Test with a **copy** of configuration in an isolated instance before changing the live deployment. Do not run two instances against the same configuration database, and do not let a test instance control production downloads or write to production media.

Database compatibility with an older image is not guaranteed. Restore a matching pre-upgrade configuration for rollback if migrations occurred; do not assume a downgrade is safe merely because the container starts. The October 27 validation of one installation is not a universal migration guarantee.

Container updates require recreating the container using the reviewed replacement image and preserved settings. Pulling an image and restarting an existing container does **not** select the newly pulled image. This project does not offer an in-app updater for custom builds. Keep image selection and automatic-update tools under explicit control.

## Metadata and support

Inherited metadata options include TheTVDB, TMDB, AniList and MyAnimeList/Jikan. TMDB-backed features require a TMDB API key; IMDb ordering requires the inherited dataset task. See [upstream design notes](../FORK.md) for background, recognizing that those notes describe inherited implementation rather than a new validation of every provider.

Report issues to [this fork](https://github.com/gaboy498-byte/Sonarr-Pro/issues), including the source commit and sanitized logs. Do not post keys, backups or private configuration. [Security reporting](../SECURITY.md) has different disclosure requirements.
