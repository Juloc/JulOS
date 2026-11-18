# Development

## Test instance (use it for every change)

```bash
deploy/dev/up.sh        # builds the server, starts PostgreSQL, guacd, the server and a throwaway SSH target
deploy/dev/test.sh      # runs the tests against the test instance's PostgreSQL
```

- The server is on `http://127.0.0.1:18080`, PostgreSQL on `127.0.0.1:45432` (both only local).
- `up.sh` generates random throwaway credentials in `deploy/dev/.env` on first run. The file is not in the repository.
- The SSH target is reachable from the server container as `test-ssh:2222` with user `tester` and the password `DEV_TARGET_PASSWORD` from `deploy/dev/.env`.
- Stop with `docker compose -f deploy/dev/compose.yaml --env-file deploy/dev/.env down` (add `-v` to also drop the database).

## Build and test without Docker

```bash
dotnet build JulOS.slnx -c Release
dotnet test --project tests/JulOS.Backend.Tests/JulOS.Backend.Tests.csproj    # database tests are skipped without JULOS_TEST_DB
```

## Layout

See [ARCHITECTURE.md](ARCHITECTURE.md). SQL migrations are numbered files in `database/`; they are embedded into the backend and applied at start-up, each once, in a transaction.
