# Releasing

JulOS uses [Semantic Versioning](https://semver.org/). The version number lives in **one** place: `<Version>` in `Directory.Build.props`.
Everything else reads it: the server (`/api/health`, `/api/about`), "Über JulOS" in the desktop menu *Hilfe* and in the phone *Profil*,
and the Docker image tag.

1. Decide the number (`0.x`: while things can still change; `MINOR` for new features, `PATCH` for fixes).
2. Set `<Version>` in `Directory.Build.props` and move the notes of `CHANGELOG.md` from *Unreleased* to the new version with the date.
3. Tests green (`deploy/dev/test.sh`, and `deploy/dev/test-display.sh` if the display engine changed), commit on `main`.
   Optional but worth the minute before a tag: `deploy/dev/e2e.sh` (browser tests: setup and login, connections, RDP picture, keyboard and mouse, SSH terminal, phone and desktop layout; see [DEVELOPMENT.md](DEVELOPMENT.md#browser-tests-real-browser-playwright-in-docker)).
4. Tag it with the same number and push: `git tag vX.Y.Z && git push origin main vX.Y.Z`. The workflow `docker` **refuses a tag that does not
   match `<Version>`**, and builds `ghcr.io/juloc/julos:X.Y.Z` (amd64 and arm64) with the commit and the build time in "Über JulOS".
5. Roll out: change the image tag in `JulOS/docker-compose.yml` of the stack repository and sync the stack in Dockhand. A deploy cuts open
   connections for a moment: tell the user first.

Builds of `main` without a tag are published as `latest` and `main` and show the version of the file with the commit behind it.
