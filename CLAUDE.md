# JulOS: working rules

Short list. If a rule does not help, delete it.

1. **Scope first.** Read [docs/SCOPE.md](docs/SCOPE.md) before starting. One open item at a time. New ideas go into [docs/IDEAS.md](docs/IDEAS.md), not into code.
2. **Design in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).** Minimal APIs, plain SQL, no ORM, no framework layers for their own sake.
3. **Nothing private in the repo or its history.** No IP addresses of the home network, no domain names, no hostnames, no keys, tokens, passwords, `.env` files or personal paths. Use placeholders in examples. Check `git diff` and `git log -p` before every push.
4. **Security is not traded for convenience.** Every API and WebSocket needs a session; no exposure to the internet without it.
5. **Never type a password for the user.** Credentials are entered by the user. Test accounts use throwaway values on localhost only.
6. **Tested means done.** Unit and integration tests for server logic; phone testing for the client. Say what was not verified.
7. **Small and readable.** Prefer a few clear functions over abstractions. Comment the reason, not the line.
8. **Docs change with behaviour.** Update the README and the relevant page in `docs/` in the same commit.
9. **Commits** are small, with a clear subject. Push only to `main` of this repository, never force.
