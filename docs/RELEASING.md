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

## Updates and security (also without a release)

A running JulOS keeps getting older even when the code does not change: the fixes for .NET, the Ubuntu base image (`libssl`, `libicu`, ...), `libopenh264`, the GPU driver and PostgreSQL arrive in the **images**, not in the repository. So:

1. **Rebuild and roll out at least once a month, and after every .NET servicing release** (second Tuesday of the month, Microsoft publishes 10.0.x): a patch release (`0.x.y`) with an unchanged code base is fine, the tag builds the image on fresh base layers. Then `docker compose pull` for PostgreSQL and recreate. Check the version in "Über JulOS" (runtime) afterwards.
2. **Dependabot** (`.github/dependabot.yml`) opens pull requests weekly for NuGet packages, GitHub Actions (pinned to commit SHAs, the comment names the version), Dockerfile and Compose images. Read them, run the tests, merge. Security updates arrive at once, outside the schedule.
3. **Weekly check** `supply-chain` (`.github/workflows/supply-chain.yml`): `dotnet list package --vulnerable --include-transitive`; it fails when a package has a known vulnerability, so a red run means: update the package (or its parent, for a transitive one) and release a patch version. The list of outdated packages is in the run summary.
4. **Vendored JavaScript** (`wwwroot/lib/xterm`, see `lib/README.txt`) is not covered by Dependabot: on each release look at the xterm.js releases and advisories.
5. The **host agents** update only on your command (or automatically if you switched that on per host); they come from the server's image, so the monthly rebuild also refreshes what the hosts get. Python on the hosts is the distribution's: keep it patched there.
6. After any restore from a backup, end all sessions (`tools/julos-user.sh logout NAME`): the dump holds the session hashes of that day.
7. Review of this process and of the whole app: [SECURITY-REVIEW.md](SECURITY-REVIEW.md) (redo it after big features: new routes, new agents, new files access).
