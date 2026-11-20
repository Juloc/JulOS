# Push messages (no AI)

Status: **built** (first stage). Decisions from the owner's interview on 2026-11-20 ([ASSISTANT.md](ASSISTANT.md), alerts). Fixed rules, no AI: JulOS tells you on your phone (or in any browser that can do Web Push) when something happens on a host. Optional AI-written messages can come later on top of this; nothing here depends on them.

## What you get

- **Rules** (each can be switched off), evaluated for every host that has an agent:
  - **New security updates** – the number of security updates rises compared with the last reading.
  - **Disk almost full** – a mount point reaches the threshold (default 90 %, 50–99 %, per mount point). Read-only images (squashfs, iso9660) are ignored.
  - **Service or container failed** – a systemd unit that was not failed before, or a Docker container that was running and is now stopped, dead or restarting. A container that never ran since JulOS watches does not count.
  - **Agent offline** – the agent has been disconnected longer than a set time (default 10 minutes, 5–1440).
  - **Restart needed** – the "reboot required" flag appeared (not while it simply stays).
  - **Back to normal** (optional message): on for *agent offline* by default, off for disk and services; there is none for security updates and restart.
- **Quiet hours** (for example 22 to 7 h, also over midnight, in the time zone of the device that switched messages on): nothing is sent during that time; afterwards **one collected message** follows.
- **One place to see it all:** Settings, **Push-Meldungen** (phone: Profil, desktop: Einstellungen) lists the devices, the rules, the quiet hours and the **last 50 messages** (what, when, which host, and whether it was delivered). A tap on a message opens the host page.
- **Test message** button; each device can be removed there.

## Turn it on (phone)

1. Open JulOS over **HTTPS** (Web Push does not work over plain HTTP; `localhost` is fine for tests). On an iPhone JulOS must be added to the Home Screen first.
2. Settings (Profil), **Push-Meldungen**, **Auf diesem Gerät einschalten**, allow notifications when the browser asks.
3. Send a **test message**.

**Brave on Android:** turn on *"Use Google services for push messaging"* in Brave's settings (privacy), otherwise the browser's push service refuses the registration (JulOS shows this hint). If the browser blocked notifications for the site, allow them in the browser's site settings.

## How a message is formed and sent

- **Model:** the browser creates a *push subscription* (an address at the browser's push service and two keys). JulOS stores it per user and device (`push_subscriptions`) and sends to it with **Web Push**: RFC 8030 (delivery), RFC 8291 with `aes128gcm` (encryption, RFC 8188) and RFC 8292 (VAPID, ES256). Written with the crypto parts of .NET (`ECDiffieHellman`, `ECDsa`, `AesGcm`, HKDF), about 150 lines in `WebPush.cs`, no third-party library: the format is small and fixed, and the test vectors of RFC 8291 appendix A prove it.
- **VAPID key pair:** created the first time someone switches messages on (one pair for the server). The private half is stored **encrypted** with the server key (`SecretBox`, AES-256-GCM, the key is not in the database), never in an API answer or log. Without a server key (`JULOS_SECRET_KEY` or the `/data` volume) messages are off and the page says so. If the pair were replaced, devices would have to switch messages on again; there is no button for it.
- **What the push service sees:** only the **encrypted** message and the address of the app (as the VAPID `sub`, taken from the page's origin when the device was added). It cannot read it.
- **What the message holds:** host name, rule code, kind (alert or ok) and the host id for the link, nothing else: **no paths, no output, no secrets**. The text ("Disk almost full") is composed by the service worker on the device (`sw-push.js`) in the device's language. Several events in one round are **one** message (title "JulOS: 3 messages", up to 8 lines, `tag` against duplicates).
- **Delivery:** 10 s per attempt, at most **two retries** (after 1 s and 3 s) for 5xx, 429 and no answer; 400, 401, 403 and 413 are not retried. **404 and 410 remove the subscription.** At most 30 sends per user per hour (test button: 5 per minute).
- **Rule engine:** the worker `AlertWorker` runs every **5 minutes** (the first time 60 s after the start, so agents can reconnect). It reads the same answers as the host page through `HostInfoService`: if the 60 s memory is still fresh, that costs nothing, otherwise one normal request per kind and host with the usual deadlines. Hosts without an agent are skipped; what an agent cannot answer stays "unknown" and keeps the old state of that rule. The rules are pure functions (`AlertRules.Evaluate`: reading and last state in, events and new state out), stored per host in `alert_state`.
- **Reported on change, not on every reading:** an event fires when a host moves *into* a state. Per host and rule at most **one message every 30 minutes**; what appears during that pause is not lost, it is reported with the first reading after it. Switched-off rules still follow the state silently, so switching one on does not flood you with old problems. The first reading of a host is the baseline for security updates and restart (so nothing old is announced); a full disk or a failed service that is already there is reported once. A disk counts as normal again only 2 points below the threshold (no flapping).
- **Not delivered:** a message that cannot be delivered (all devices fail) is tried again at the next round and dropped after 24 hours (longer than any quiet time); with no device it only stays in the list ("only here").

## Limits and decisions

- **Per user:** at most 10 devices. The settings are per user, the VAPID pair per server.
- **The endpoint comes from the browser**, so the server checks it hard (see below). Only `https`, port 443, a real name; no IP numbers, no `localhost`, `.local`, `.internal`.
- **Offline for the whole server:** if JulOS itself is down, nothing can be sent. An external check is the answer to that, not this feature.
- **Phone offline:** the push service holds a message up to 12 hours (`TTL`), then it is dropped; the list in the app still has it.
- **Not built (later):** per-host rules, snooze, a button inside the notification, AI-written summaries, the `pushsubscriptionchange` re-registration (a device whose browser rotated the subscription is simply removed on the next 404/410 and switched on again).
- **Verified here:** the encryption against the RFC 8291 vectors, the signature of the VAPID token, the rules, the delivery path (retry, dead subscription, limit) against a fake push service in memory, the page on phone and desktop layout. **Not verified here:** a real phone and a real push service (Google, Mozilla, Apple); the first real message is the test button.

## Privacy and security

Details in [SECURITY-REVIEW.md](SECURITY-REVIEW.md#push-messages-review-of-the-branch-push-meldungen-2026-11-20). In short: every route needs a session (and header and origin for changes), a subscription belongs to the user who created it, the server only connects to public addresses (checked again when connecting, against DNS tricks), and the private VAPID key is encrypted at rest.

## Where it lives

`database/022_push.sql` · `WebPush.cs`, `PushPayload.cs`, `AlertRules.cs` (Backend, Logic) · `PushStore.cs` (Data) · `PushService.cs` (Services) · `AlertWorker.cs` (Workers) · `HostObserver.cs`, `Push/PushEndpoints.cs` (Frontend) · `alerts.js`, `alerts.css`, `sw-push.js` (wwwroot). Tests: `WebPushTests`, `AlertRuleTests`, `PushDeliveryTests`.

API (all behind a session): `GET /api/push` (state, devices, rules, last 50), `GET /api/push/key` (public VAPID key, creates the pair), `POST /api/push/subscriptions`, `DELETE /api/push/subscriptions/{id}`, `PUT /api/push/settings`, `POST /api/push/test`.
