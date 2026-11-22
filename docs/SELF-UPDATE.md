# JulOS updates itself from the app (plan, nothing built yet)

Status: **plan for the owner's decision.** Written 2026-11-22. Owner's wish: the app shows the new version with release notes and a button; the server never touches the container runtime itself; a health check and an automatic way back. Principle: settings live in the app ([BACKLOG.md](BACKLOG.md)). Effort: **S** = hours, **M** = a day or two, **L** = several days.

How it runs today: JulOS is a Compose stack on a Docker VM, managed by Dockhand. Dockhand takes the stack from a private Git repo, where the image is pinned (`ghcr.io/<owner>/julos:X.Y.Z`). A release = push a tag, the `docker` workflow builds the multi-arch image (about 70 s), the pin is raised in the stack repo, then "sync from Git" and "deploy" in Dockhand ([RELEASING.md](RELEASING.md)).

## A. What the owner sees, and where the information comes from

1. **Detect.** The server asks GitHub about once a day (setting) for releases of this repository. A newer version than `AppInfo.Version` appears as a quiet note in "Über JulOS", in the settings (section *Updates*) and on the start page; a push message once per version ([PUSH.md](PUSH.md)).
2. **Read.** The note opens the release notes (the German `CHANGELOG.md` section) as plain text with the version, date, and **warnings** that matter: "needs a restore to go back" (see B, migrations), "connections drop for a moment".
3. **Button.** What the button does depends on the variant in B. Smallest: opens the Compose manager / copies the one line to change.
4. **Progress and result** (only with a real "Update now"): *Backup made* → *new image checked* → *container replaced* → *waiting for the new server* → *done, now 0.24.0* or *failed, back on 0.23.0*.
5. **Way back.** Automatic only if something outside JulOS watches the new server (see B). Otherwise the page names the previous version and the exact manual step.

**Source: public GitHub releases, read only, no account, no token.**
- `GET https://api.github.com/repos/<owner>/julos/releases?per_page=10` (fixed host in code, never from a setting or from a response). Not `/releases/latest`: it skips pre-releases, and **all current releases are flagged *Pre-release*** (checked: `/releases/latest` answers 404 today). The channel setting decides: *stable* = not draft, not pre-release; *pre-release* = also those. Until the owner decides what `0.x` counts as, the workflow must set the flag deliberately (step 0).
- **Cache and rate limit.** Unauthenticated calls are limited to 60 per hour per IP ([GitHub docs](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)). One call per interval (default 24 h, minimum 1 h), `ETag`/`If-None-Match` (a `304` costs nothing), the result in the database, "check now" at most once a minute. On any error: keep the last result, back off, never a popup.
- **Off switch.** Setting *Updates: check for new versions* (on/off). Off means no outbound call at all. Default: to decide (E1).
- **The digest belongs to the tag.** Today the tag `vX.Y.Z` and the image tag `X.Y.Z` are only linked by name, and tags can move. So the workflow writes the digest of the pushed manifest (`docker/build-push-action` outputs it) and the commit into the release (one line in the notes or a small `julos-release.json` asset). Before anything is deployed, the helper asks `ghcr.io` for the manifest of `X.Y.Z` (anonymous token, `HEAD /v2/<owner>/julos/manifests/X.Y.Z`, header `Docker-Content-Digest`, [OCI spec](https://github.com/opencontainers/distribution-spec/blob/main/spec.md)) and compares; the deploy then uses `image@sha256:...`. **Honest limit:** the release text and the image come from the same repository; this check stops a moved tag or a wrong push, not someone who controls the repository. A signed build attestation (`actions/attest-build-provenance` plus a check) closes that later (**L**, not planned).
- **Untrusted text.** Release notes come from the network: shown as text or a tiny whitelisted Markdown subset, never as HTML, links only `https`.

## B. Who does the update? Variants, compared honestly

What I found about Dockhand (official manual <https://dockhand.pro/manual>, repo <https://github.com/Finsys/dockhand>; the unofficial documentation mirrors I first found are gone, so every API path below is **to verify** in a spike):
- Each Git stack can have a **webhook** (`/api/git/stacks/{id}/webhook`; GitHub-style HMAC signature, GitLab token header, or `GET ?secret=`). It syncs and redeploys **only if the Git commit changed** (unless "Force redeployment" is on). A scheduled auto-sync (cron) behaves the same.
- The stack option **"Re-pull images"** adds `--pull always` on deploy.
- An HTTP API with `Authorization: Bearer dh_…` tokens exists; the manual describes a token as **full access**, no scopes found. Dockhand can update containers on a schedule. No write-back to Git is documented. Whether Dockhand waits for `healthy` or rolls back: **not found**.

| | Security | Effort | The Git pin | Automatic way back |
|---|---|---|---|---|
| **1. Sidecar updater with the Docker socket** (own small container; Watchtower-like) | Socket = root on the VM, whatever the updater's size. A socket proxy cannot limit by container ([INTEGRATIONS.md](INTEGRATIONS.md)). Must listen only on the internal network, accept only "update to this verified digest", never a name from the browser. Watchtower itself does not fit: archived in December 2025 ([repo](https://github.com/containrrr/watchtower), maintained fork [nicholas-fedor/watchtower](https://github.com/nicholas-fedor/watchtower)), and its HTTP API updates "everything", not one chosen digest | **L**: a new image and release line, recreating a Compose-managed container by hand (inspect, create, start) or with a Compose file on the host, tests against a real runtime | **Drifts.** Git still says 0.23.0, the VM runs 0.24.0. Dockhand redeploys only when Git changes, so the next unrelated commit to the stack repo (or a manual "deploy") **silently rolls JulOS back**. Dockhand also shows a stale state | **Yes**: the sidecar outlives the restart, checks `/api/health` version, restores the old image |
| **2a. JulOS bumps the pin in Git, then pokes Dockhand** | New secret: **write access to the stack repo** (a fine-grained token cannot be limited to one file) and the Dockhand webhook secret (small power: it can only redeploy what Git already says). Mitigation: a dedicated tiny repo for the JulOS stack, or pin changes via pull request. No Docker socket anywhere near JulOS | **M-L**: Git write client, webhook call, wait for the new server | **Correct**: Git stays the truth, Dockhand shows the right state | **No** by itself: the server that checks is the one being replaced. If it never starts, nobody reverts the commit. Only a manual revert in Git. Unless Dockhand turns out to support health-gated deploys (spike) |
| **2b. JulOS calls the Dockhand API** (change a version variable, deploy) | A full-access Dockhand token in JulOS: whoever breaks JulOS controls every stack on every host. **Worst secret of all variants** | M | Needs the version as a variable in Dockhand instead of the pin in Git | Same as 2a |
| **3. Notice plus a button that opens Dockhand / copies the line** | **No new secret, no new right.** The Dockhand address is a setting in the app, opened in a new tab (`noopener`) | **S** | Untouched: the owner still raises it | No, and none needed: the owner is there |
| **3b. Same as 3, but the bot raises the pin** (my addition): Dependabot (already used in this repository) or Renovate on the stack repo opens a pull request when a new `julos` image tag appears; merging it is the approval; Dockhand picks it up on its cron sync or the owner's click | Secrets stay in GitHub and Dockhand, **JulOS has none**. The pin stays in Git, as the owner wants it | **S** (a small config file in the stack repo, no JulOS code) | Correct | No (revert the pull request) |
| **4. Combination** | 3 and 3b now, 2a only if "Update now" is still wanted after living with it | S now, M-L later | Correct | See 2a |

**Recommendation: 4, in this order: 3 now, 3b next, an executor later and only if still wanted.**
- A release takes the owner about two minutes today (raise pin, sync, deploy). The honest gain of a real button is small; the cost is a new root-level or write-level secret on an internet-facing server. That does not fit the project's size.
- If the button is built anyway, **2a beats 1**, because Git stays the truth and no socket exists. Variant 1 is the only one with an automatic way back, but it buys that with root on the VM and a pin that silently drifts back at the next stack commit. I would not build it unless the drift problem is solved (it is not).
- **Risk of the recommendation:** "automatic way back" is not delivered by 3, 3b or 2a. What the app can do instead: make a verified backup first, show the previous version and the revert step, and after a restart show a red note "the new version did not become healthy" (only when the new server is up; a dead server cannot tell).

**Database migrations** ([Migrator.cs](../src/JulOS.Backend/Data/Migrator.cs), 23 numbered scripts): they run at start, each in its own transaction under an advisory lock, never twice, there are no down scripts. A failing script rolls back and leaves the database at the last good script; the server does not start, and the old image can start again on that schema **as long as the earlier scripts of the same start were additive**. The backup dump holds data only; a dump from an older JulOS restores into a newer one, not the other way ([BACKUP.md](BACKUP.md)). So:
- **Backup before every update, not optional** (in the app; E3). The updater makes a dump through the existing backup service, checks it exists and is not empty, and notes the highest migration. No fresh dump, no update.
- **Release rule:** a migration must keep the previous version working (add, never drop or rename in the same release). A release that cannot do this says so in its notes (*"going back needs a restore of the backup from before the update"*), and the app shows it before the button.
- Real rollback after a bad but successful migration = old image plus restore of the pre-update dump (data written since is lost). That stays a manual, documented step ([BACKUP.md](BACKUP.md)), never automatic.

## C. Security rules (whatever the variant)

- Only a signed-in user; every route behind the session (`ApiGuard`), listed in the route security test. A *check* needs only the session; *settings* and *update now* need the second factor: add them to `StepUpPolicy` (a fresh TOTP, 10-minute window) and use the **two-step confirmation** of host actions (prepare, then confirm with a one-time token, level "critical" with a tick; [HOST-AGENT.md](HOST-AGENT.md#the-confirmation-two-steps-on-the-server)). Closing the window never confirms.
- **Audit entry** for every setting change, check result, start, success, failure, rollback (store like `FileAuditStore`, no secrets in it).
- **Pause switch:** the existing "all actions paused" switch also stops updates. There is no schedule that installs by itself (E2).
- **No update without a fresh backup**, and none while a host action, a backup or a restore runs.
- **Digest rule:** only a digest that the release names and the registry confirms is ever deployed; the version string comes from the release list, never from the browser.
- **The Docker socket never enters the JulOS container or its network.** Any helper (variant 1) has only the fixed image name, no shell, read-only file system, `cap_drop: ALL`, `no-new-privileges`, an internal network shared only with JulOS, and one authenticated verb.
- Secrets (a Git token, a webhook secret) live encrypted in the database like the stored host passwords ([BACKUP.md](BACKUP.md), key handling), are never shown again and never logged. Nothing private in the repo or its history: the docs and workflow use `<owner>` and placeholders.

## D. Steps, each releasable alone

| # | Step | Files | Effort |
|---|---|---|---|
| 0 | **Release pipeline:** the workflow creates the GitHub Release from the `CHANGELOG.md` section with the digest and commit; sets *Pre-release* only for real pre-releases; `RELEASING.md` gets the steps (including the migration rule) | `.github/workflows/docker.yml` (or a new `release.yml`), `docs/RELEASING.md` | S |
| 1 | **Check and notice (variant 3):** `UpdateCheck` (pure: version compare, channel filter, release parsing, notes cleaning), hosted service with interval, ETag and cache; settings *Updates* (on/off, channel stable/pre-release, interval); `GET /api/updates`, `PUT /api/updates/settings` (step-up), `POST /api/updates/check`; fixed-host outbound client (api.github.com only, no redirects, size limit, timeout) | `src/JulOS.Backend/Logic/UpdateCheck.cs`, `src/JulOS.Frontend/Updates/UpdateEndpoints.cs`, `Program.cs`, `Json/AppJson.cs`, `Auth/StepUpPolicy.cs`, `database/024_updates.sql`, `tests/JulOS.Backend.Tests/RouteSecurityTests.cs` | M |
| 2 | **Interface:** settings section *Updates*, note in "Über JulOS" and on the start page and phone *Profil*, notes view, "Open Dockhand" (address is a setting) and "copy line"; bump `sw.js` `VERSION` and add the new file to the shell list | `src/JulOS.Frontend/wwwroot/` (new `updates.js`, settings, about, start), `sw.js` | S-M |
| 3 | **Push** "new version" as one more rule (once per version, quiet hours) | `docs/PUSH.md`, push rules code | S |
| 4 | **Bot raises the pin (variant 3b)** in the stack repo, written down in `RELEASING.md` | stack repo (outside this one), `docs/RELEASING.md` | S |
| 5 | **Spike on a test stack:** read Dockhand's API reference, try webhook, token and "Re-pull images", find out whether it health-gates a deploy. Output: a page, no code | notes only | S |
| 6 | **"Update now" (variant 2a, only after the owner decides):** prepare/confirm, backup, digest check, pin commit (dedicated repo or pull request), webhook, wait for the new server, report | `Updates/UpdateRunner.cs`, `GitPinWriter`, `DockhandHook`, audit store, UI progress | M-L |

**Tests (rule 6: only security-critical and pure logic).** Steps 0-3: version and channel logic, release parsing, digest comparison, notes cleaning (no HTML gets through), ETag and backoff; route test: every `/api/updates*` route needs a session and the writes need step-up; the fixed host cannot be changed by a setting or a redirect. Step 6 adds: no run without a fresh backup, wrong or unconfirmed token refused, pause switch honoured, audit written. No test for plain display; the interface is checked by running it (fake release list on localhost in the browser pane). Not verifiable without the real stack: Dockhand behaviour and the real `ghcr.io` call (spike and one live check).

## E. Open questions for the owner

1. **Default of the check:** on or off after an update of JulOS? (It makes one outbound call to GitHub a day.)
2. **Channel:** are `0.x` releases "stable"? (Today GitHub flags them *Pre-release*.) Pre-release channel needed at all?
3. **Backup duty:** is "no update without a fresh backup" a hard rule (my advice) or a warning? What if no backup folder is mounted?
4. **Automatic or only on button:** my advice is never silent installation; a push message at most.
5. **Dockhand access:** allow a Dockhand webhook secret (small power) in JulOS? A Dockhand API token (full access, my advice: no)? Git write access, and if so a dedicated repo or pull requests?
6. **Is "Update now" worth it** at all, given about two minutes per release by hand and no automatic way back with 2a? Or stop after 3 and 3b?
7. May the Dockhand address be stored in the app (for the "Open Dockhand" button)?

## Sources

- Dockhand manual and repo: <https://dockhand.pro/manual>, <https://github.com/Finsys/dockhand> (read by a helper; large parts only as a summary, therefore "to verify")
- GitHub REST rate limits (60 per hour unauthenticated, per IP): <https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api>; releases API: <https://docs.github.com/en/rest/releases/releases>
- OCI distribution spec (manifest `HEAD`, `Docker-Content-Digest`, optional header): <https://github.com/opencontainers/distribution-spec/blob/main/spec.md>
- Watchtower (archived 2025-12-17) and fork: <https://github.com/containrrr/watchtower>, <https://github.com/nicholas-fedor/watchtower>; its HTTP API: <https://github.com/containrrr/watchtower/blob/main/docs/http-api-mode.md>
- In this repo: [RELEASING.md](RELEASING.md), [BACKUP.md](BACKUP.md), [HOST-AGENT.md](HOST-AGENT.md), [PUSH.md](PUSH.md), [INTEGRATIONS.md](INTEGRATIONS.md), [SECURITY-REVIEW.md](SECURITY-REVIEW.md)
