# Files app as a real explorer (specification)

Status: **planned**, requested by the owner on 2026-11-20. Not built yet. It follows the root-file work (`files-root`, see [FILES.md](FILES.md)) because both change the same screens.
The goal: the Files app feels like the Windows Explorer the owner knows, on the desktop with a mouse and keyboard, and stays usable on the phone.

## What the owner asked for

### Behaviour (mouse and keyboard)

- **One click selects, double click opens** (folder: go in; text file: opens the editor; other files: download or preview).
- **Right-click menu** on a file, on a selection, on a folder and on the empty area: open, open in new window, download, copy, cut, paste, rename (F2), delete (Del), new folder, new text file, upload, properties, copy path, pin to the sidebar.
- **Selection like the Explorer:** `Shift` selects a range, `Ctrl` adds or removes single items, `Ctrl+A` selects all, a rubber band (drag on empty space) selects what it touches, `Esc` clears.
- **Type to jump:** typing letters jumps to the first item whose name starts with them; typing a whole word quickly keeps narrowing (the buffer clears after about one second). Arrow keys move the selection, `Enter` opens, `Backspace` or `Alt+Left` goes up, `Alt+Right` forward, `F5` refreshes.
- **Cut, copy and paste** with `Ctrl+X`, `Ctrl+C`, `Ctrl+V` within one place and **across hosts** (the existing server-side stream copy). Drag and drop between folders, between the sidebar and the list, and between two windows. Every write still goes through the server-issued confirmation of [FILES.md](FILES.md); a confirmation is one small sheet per action, never one per file of a selection.
- **Edit files:** a text file opens in a simple editor window (plain text, monospace, size limit 1 MB, line numbers, `Ctrl+S` saves through the confirmed write, a clear "changed" marker, a warning when the file changed on the host since it was opened). Binary files are never opened in the editor.

### Look

- The buttons at the top right of the window are untidy. Replace them by one tidy toolbar: back, forward, up, an address bar that shows the path as clickable segments and turns into a text field on click, a search field, **View** menu (list or tiles, compact or comfortable, sort), and **New** and **Upload**. Icons are SVG, never emoji.
- **Compact list is the default** (small rows, columns: name, date, type, size). Columns are sortable by clicking the header and resizable.
- **File extensions are always shown**, with no setting and no checkbox.
- **Hidden files are shown by default, but dimmed** (a lighter text colour and icon). The View menu has a "Hidden files" switch to hide them; the choice is remembered per user.
- The sidebar **lists every host as a tree**: a host expands to its places (home folder, the root place when the agent allows it, the shared upload folder) and to the **pinned folders** of that host. Any folder can be pinned from its context menu ("Pin to sidebar"); pins are stored per user and survive a restart; they can be reordered and unpinned. Collapsed or expanded state of each node is remembered.
- A status bar at the bottom: number of items, size of the selection, the place and the user it works as. The root place keeps its clear warning banner.

### Phone

The phone keeps its own layout (list with long-press for the context menu and multi-select, a bottom bar for the actions); the same menu entries, the same shortcuts where a keyboard is attached.

## Order of work

1. `files-root` (in progress) and the host login (one user per host, see below) land first.
2. The explorer rework: first the interaction model (selection, keyboard, type to jump, context menu, drag and drop), then toolbar, view settings and the sidebar tree with pins, then the editor.
3. Tests: a pure-logic module for selection, type-to-jump and sort (node tests), the existing server tests unchanged, one browser flow in `deploy/dev/e2e.sh` (select with Shift and Ctrl, type to jump, context menu, rename with F2, pin a folder).

## Notes on credentials (decision of 2026-11-20)

A host has **one login (user name and password)** that every app of that host uses: terminal, files over SFTP, the desktop connections unless one of them sets its own. Root is **not** a second stored password: root actions and root files go through the agent (`files-root`, host actions), which needs no password. A root terminal is `sudo -i` or `su` typed by the owner in the terminal.

Status: built in the branch `claude/host-login` (see [HOSTS.md](HOSTS.md#login-one-user-and-password-per-host)): the host login, the tick "Eigenen Benutzer verwenden" per connection, and the Files question that asks only for a missing password.
