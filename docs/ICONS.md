# Icons: one family, one mapping

Tiles, the dock, the thin pills, window titles, the connection lists of the desktop and the phone, and the host cards all get their icon from **one place**:
`wwwroot/icons.js`. Nobody picks an icon by hand; the user can override one per connection and go back to "automatic".

## The family

- About 50 small stroke icons on a 24 px grid. The ones for operating systems, hosts and programs are **drawn for JulOS**; the older everyday ones (folder, cloud, globe, shield ...) were already in the
  desktop and follow the style of the Feather set (MIT), so `licenses.txt` names it. No brand logos. They draw with `currentColor`; shapes with an area carry `class="f"`, so the style "filled" can tint them (`icons.css`).
- Operating systems and hosts have **neutral** symbols, not logos: Windows is a window with four panes, Linux a prompt in a circle, Proxmox layers, Home Assistant a house with a
  signal, Synology a storage box, macOS a laptop, a Raspberry Pi a chip. `PICK_KEYS` is the part of the family offered in the picker (28 icons), `COLORS` the 11 tile colours
  (a gradient each; the white icon reaches 3:1 on both ends, tested).
- File type icons of the Files app are not part of this and stay as they are.

## The mapping (pure functions, `tests/frontend/icons.test.mjs`)

`iconFor({ conn, host, override })` returns `{ key, color, g, auto }`. Order:

1. **The user's override** for this connection (icon and colour each on their own).
2. **Kind and program:** Claude Code (sparkle), Codex (brackets), a custom command (the first word: `docker` containers, `htop` activity, `vim` pencil, `tmux` grid, `psql` database ... else a bolt),
   a plain shell (terminal), VNC (screen with pointer), RDP (screen).
3. **The operating system of the host, when it says something** (`osFamily(host)` from what JulOS already knows: `os`, `hostname`, `agent.platform`): an RDP desktop of a Windows machine gets the
   Windows symbol; a shell on Proxmox, Home Assistant, Synology or Windows gets the symbol of the host; a plain Debian or Ubuntu shell stays a terminal (that says nothing new).
   Programs keep their own icon on every host.

`appIconFor(id)` does the same for the programs of the desktop (Start, Files, Hosts, Settings ...), `hostIconFor(host)` gives a host its icon by operating system (host cards and the
section headers of the connection list), `connectionIcon(conn, hosts, appearance)` is the one-liner that desktop and phone call.

## Overrides

In the dialog of a connection (desktop and phone) the row **Icon** shows the current icon, "Change ..." opens the 28 icons and 11 colours, "Automatic" removes the override.
It is saved at once in the setting `appearance` (`icons.over`, at most 200 entries, key = connection id), per user on the server, so it follows to every device.
See [APPEARANCE.md](APPEARANCE.md).

## Not done

- No brand logos (Debian, Docker, Proxmox ... have trademark rules); the neutral symbols above stand in.
- JulOS does not yet know the operating system of every host (it comes from the agent); without it a connection shows its kind. A Home Assistant or Synology host is recognised only when the agent
  or the host name says so.
