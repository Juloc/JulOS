# Quick Connect, folders, favourites and settings on the server

Three small features that make the connection list easier to live with. None of them adds a new service or a new container.

## Quick Connect

A one-off session to any address without saving a connection. Phone shell: "Quick Connect" button on the Services page, and typing an address into the search offers
"Quick Connect to ...". Desktop: menu Window, the quick action on the start page, and the launcher (type an address). The dialog asks for the kind (Desktop = RDP,
Terminal = SSH), the address (also `user@address:port`, `rdp://`, `ssh://`), the user name and the password.

- **Nothing is stored.** No row in `connections`, `hosts` or any other table; the password lives only in the ticket (30 seconds, memory) and in the session. It is
  not written to the log. The browser keeps no zoom or window-bar setting for a quick target either.
- **Same rules as a saved connection.** `POST /api/remote/ticket` takes `quick: { kind, host, port, username }` instead of `connection`; the input goes through
  `ConnectionRules.Validate` (host pattern, port range, kind, user name length), the allow-list `JULOS_ALLOWED_HOSTS` applies (403 otherwise), and the endpoint is behind the
  session check and `ApiGuard` like every other one. A password is required (400 without).
- **Rate limit.** At most 12 quick tickets per user and minute (429 beyond), so the feature cannot be used to probe many targets quickly.
- **Trust on first use, in memory only.** The SSH host key and the RDP certificate fingerprint of a quick target are remembered in server memory (`QuickTrust`, at most 500
  entries, forgotten at restart), so a target that changes while the server runs still triggers the warning. They are not written to the database.
- **One slot per target.** The session slot is derived from kind, address, port and user name (`QuickConnect.IdOf`), never the id of a stored connection; opening the same
  target again takes the existing session to the front (a second one only replaces it at the server slot).

## Folders

A folder groups connections (name 1 to 40 characters, one of 12 symbols, position). At most 30 folders per user. A connection is in at most one folder or in none.

| Piece | Where |
| --- | --- |
| Table `folders` (user, name, icon, sort), columns `connections.folder_id`, `favorite`, `last_used_at`, `use_count` | migration `010_folders_and_settings.sql` |
| Old data | unchanged: existing connections have no folder, are no favourites and were never used; a folder delete only clears the link (`on delete set null`) |
| `GET /api/folders` | `{ folders: [...], placements: [{ id, folderId, favorite, lastUsedAt, useCount }] }`; the client lays the placements over its connection list (same id) |
| `POST /api/folders`, `PUT /api/folders/{id}`, `DELETE /api/folders/{id}`, `PUT /api/folders/order` | create, rename or change the symbol, delete (connections stay), set the order |
| `PUT /api/connections/{id}/folder` | `{ folderId }`, `null` takes it out of its folder; another user's folder or connection gives 404 |
| `POST /api/connections/{id}/move` | `{ delta: -1 or 1 }`, moves one place within the folder (or the connections without folder) |

The phone shell (Services page) shows folder chips and groups the tiles by folder; the desktop (view "Services") groups by folder or by host (switch) and filters by folder.
The connection dialog has a "Folder" field; folders are managed in their own dialog (create, rename, symbol, up, down, delete).

### Favourites and "recently used" on the server

Before, the phone kept favourites and use counts only in `localStorage` of that device. Now they are columns of the connection, so every device sees the same:

- `PUT /api/connections/{id}/favorite` (`{ favorite }`) pins or unpins; `POST /api/connections/{id}/used` is called by the client when a session is started
  (sets `last_used_at`, counts `use_count`). Both only for the user's own connections.
- **Take-over of the local state:** the first time a device talks to the server after the update, it sends its old local favourites, counters and last-start times once
  (`POST /api/folders/import-local`) and then marks them done. The server only takes over its own connections' entries, never lowers a counter and never sets a time in the
  future, so a repeated call or several devices do no harm. The local copy of the activity log ("started and ended") stays on the device.

## Settings per user on the server

The button layout of the remote view, the theme colour and the interface language are saved per user, so a second device looks the same.

- Table `user_settings (user_id, key, value jsonb)`; the keys are `keybar`, `theme` and `lang` only (`SettingsRules`). `GET /api/settings`, `PUT /api/settings/{key}` with
  `{ value }`, `DELETE /api/settings/{key}` (reset to the defaults). The server **checks and rewrites** every value: unknown fields are dropped, lengths and counts are limited
  (12 rows, 60 keys per row, label 24, value 200 characters), colours must be `system` or `#rrggbb`.
- **keybar:** the rows with their keys (selection and order). Each key has an optional `scope`: `both` (default), `desktop` or `terminal`; a session only shows the keys of its
  own kind (all keys are shown while editing).
- **theme:** `{ seed, scheme }` (colour and light, dark or automatic).
- **lang:** `{ lang }` with `de`, `en` or `system` (interface language, see [DEVELOPMENT.md](DEVELOPMENT.md#languages)); any other value is rejected.
- **Local cache and fallback (`prefs.js`):** the page keeps its copy in `localStorage` as before and uses it at once, so the layout is there without waiting and when the server
  cannot be reached. After sign-in it asks the server: a value saved there wins and refreshes the local copy; when the server has none yet, the local one is uploaded (first
  call). A change is applied locally first and pushed after a short delay; if the push fails it is retried at the next start (a "pending" mark in `localStorage`).
