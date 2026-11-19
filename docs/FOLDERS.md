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
