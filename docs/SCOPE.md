# Scope: what we build now

One page, to keep us on track. The long-term direction is in [VISION.md](VISION.md): JulOS becomes the one interface for everything
I run (remote desktop, Docker, Caddy, DDNS, files, ...). **This file says what we build right now and wins whenever the two
disagree.** The vision tells us where we are going, this file tells us what we are allowed to touch today.

## Milestone 1: the Remote module (release 1.0)

**Goal:** From my Pixel, in an installed PWA over HTTPS, I use Claude Desktop and other programs on my Linux machine comfortably
and securely, with my normal Android keyboard.

### Done when all of this works on the phone

1. **Install and reach:** the app installs as a PWA over HTTPS (Caddy), protected by client certificate and proxy secret.
2. **Connect:** login with my own password, a picture (not a black screen) within a few seconds, reconnect with one tap after a drop.
3. **Type:** Gboard suggestions and autocorrect replace words correctly; the one-row key bar has Esc, Ctrl, Tab, zoom, fullscreen, menu,
   expand and keyboard; the expanded rows hold the rest; every row is editable.
4. **Programs:** program bar at the top (mode, open windows, launcher), Claude shown narrow so its sidebar becomes a menu.
5. **Photos:** a photo or a Gboard image arrives in the remote program's input.
6. **Publish:** public repo, image on ghcr.io, and the README quick start works on a clean machine.

Nothing else blocks 1.0. If it is not on this list, it is not in milestone 1.

### Out of scope for milestone 1

The other modules (Docker, Caddy, DDNS, Proxmox, files), the module shell, Windows agent, multi-host, file browser and drag and drop,
a desktop web version, notifications, macOS, audio, multiple users, anything that needs a new service.
They are the point of JulOS later (see the vision), but they come after 1.0.

## Rules against scope creep

1. **One thing at a time.** One open item; finish it, test it on the phone, then pick the next.
2. **New idea? Write it into [IDEAS.md](IDEAS.md) and go back to work.** Ideas are never started in the same session they come up in.
3. **Order is fixed:** 1 Install and reach, then 2 and 3 (test on phone), then 4, then 5, then 6. Fixes for things already on the list come before new work.
4. **No new service, dependency or protocol** without writing down why milestone 1 cannot be done without it.
5. **Security is never traded for convenience.** No exposure to the internet without client certificate and proxy secret.
6. **Done means tested on the phone**, not just built.

## Next steps, in order

1. Push the repo, let the image build, make the package public.
2. Caddy: subdomain to the gateway, client certificate, proxy mode (`tools/enable-proxy-mode.sh`).
3. Test on the Pixel against the checklist above and write down what fails.
4. Fix only what that list shows.
5. Tag 1.0. Then plan milestone 2 (the module core) on purpose, with its own page like this one.
