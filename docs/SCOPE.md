# Scope: what we build now

One page, to keep us on track. The long-term direction is in [VISION.md](VISION.md). **This file wins whenever the two disagree.**

## Goal (one sentence)

From my Pixel, in an installed PWA over HTTPS, I use Claude Desktop and other programs on my Linux machine comfortably and securely,
with my normal Android keyboard.

## Version 1 is done when all of this works on the phone

1. **Install and reach:** the app installs as a PWA over HTTPS (Caddy), protected by client certificate and proxy secret.
2. **Connect:** login with my own password, connect in a few seconds, reconnect with one tap after a drop.
3. **Type:** Gboard suggestions and autocorrect replace words correctly; extra keys include Ctrl and Tab; Ctrl/Shift stay toggled.
4. **Programs:** window bar with app mode, a launcher, and Claude shown narrow so its sidebar becomes a menu.
5. **Photos:** a photo or a Gboard image arrives in the remote program's input.
6. **Publish:** public repo, image on ghcr.io, and the README quick start works on a clean machine.

Nothing else blocks 1.0. If it is not on this list, it is not in 1.0.

## Out of scope for 1.0

Windows agent, multi-host, file browser and drag and drop, a desktop web version, adapters and quick actions, notifications,
macOS, audio, multiple users, themes beyond Material You, anything that needs a new service.

## Rules against scope creep

1. **One thing at a time.** One open item; finish it, test it on the phone, then pick the next.
2. **New idea? Write it into [IDEAS.md](IDEAS.md) and go back to work.** Ideas are never started in the same session they come up in.
3. **Order is fixed:** 1 Install and reach, then 2 and 3 (test on phone), then 4, then 5, then 6. Fixes for things already on the list come before new work.
4. **No new service, dependency or protocol** without writing down why 1.0 cannot be done without it.
5. **Security is never traded for convenience.** No exposure to the internet without client certificate and proxy secret.
6. **Done means tested on the phone**, not just built.

## Next steps, in order

1. Push the repo, let the image build, make the package public.
2. Caddy: subdomain to the gateway, client certificate, proxy mode (`tools/enable-proxy-mode.sh`).
3. Test on the Pixel against the checklist above and write down what fails.
4. Fix only what that list shows.
5. Then 1.0 gets a tag. After that, pick from IDEAS.md on purpose.
