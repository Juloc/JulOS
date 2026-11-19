# Backup and restore

JulOS keeps accounts, connections and stored target passwords in PostgreSQL. Stored passwords are encrypted (AES-256-GCM) with the key in
`/data/secret.key` (or `JULOS_SECRET_KEY`). **Lose either one and the stored passwords are gone**; lose the database and so are accounts and connections.
So JulOS backs up both, every day, into a folder you choose. No extra container: the server does it itself.

## What is written

Into the backup folder (inside the container `/backup`), as **separate files**:

| File | What | When |
| --- | --- | --- |
| `julos-db-20261119T030000Z.sql.gz` | all tables, compressed text (UTC time in the name) | daily |
| `secret.key` | copy of `/data/secret.key` | when new or changed |

Old dumps are deleted after `JULOS_BACKUP_KEEP_DAYS` days; the newest one is never deleted, and files that are not dumps are never touched.
A dump is written as `.tmp` and renamed at the end, so a crash leaves no dump that only looks complete.

Why not `pg_dump`? The image has no matching client (Debian ships PostgreSQL 17 tools; `pg_dump` refuses a PostgreSQL 18 server), and a second container
just for this is not worth it. The server writes a plain text dump itself (`COPY` blocks, one consistent snapshot, readable with `gunzip` and `psql`).
It holds the data only: the schema comes from the migrations, so a dump from an older JulOS restores into a newer one; a dump from a newer JulOS is refused.

## Settings

| Variable | Default | Meaning |
| --- | --- | --- |
| `JULOS_BACKUP` | empty | empty: **on, but only if the backup folder is mounted**. Without it the server logs a warning at every start and "Letztes Backup" in the About window says "Nicht eingerichtet" (it never writes into the container's throw-away disk). `on`: same, but a missing folder is logged as an error. `off`: no backup, no warning. |
| `JULOS_BACKUP_DIR` | `/backup` | Folder inside the container. Must exist and be writable by the server user (it is never created). |
| `JULOS_BACKUP_TIME` | `03:00` | Time of day, `HH:MM`, in the server's time zone (UTC in the container unless `TZ` is set). |
| `JULOS_BACKUP_KEEP_DAYS` | `14` | How long dumps are kept. |
| `JULOS_BACKUP_KEY` | `copy` | `off`: do not copy `secret.key` (keep it elsewhere, see below). |

If the server was off at the usual time (or this is a new install), a dump is made two minutes after the next start. After a failure it tries again after an hour.
A manual dump: `docker compose exec julos dotnet JulOS.dll backup create`.

## Setup (Compose)

Both Compose files mount the host folder `JULOS_BACKUP_PATH` (default `~/julos-backup`) as `/backup`. Create the folder as the user the server runs as (`JULOS_UID`) and keep it private:

```bash
mkdir -m 700 ~/julos-backup
```

If Docker creates it for you it belongs to root and the server cannot write: the log and the About window say so. Then copy the folder off the machine
(another disk, NAS, `rsync`, your usual backup tool): a backup on the same disk does not survive the disk.

## The key and the dump

- The dump contains **password hashes, authenticator secrets and recovery code hashes**; it is as sensitive as the database. The backup folder must belong to you alone (mode 0700, files 0600, which the server sets).
- Stored target passwords are in the dump only in encrypted form, and the key is a **separate file**, never inside the dump. A stolen dump alone shows no target password.
- If the backup folder is shared or goes to a place others can read, set `JULOS_BACKUP_KEY=off` and keep `secret.key` somewhere else (for example in your password manager). Then the folder alone is of no use for target passwords. Whoever has dump **and** key can read them, so do not put both in the same unprotected place.
- With `JULOS_SECRET_KEY` in `.env` there is no `secret.key`; back up `.env` yourself.

## Restore

Pick the dump and make sure you have the matching `secret.key`. On the Docker host, in the stack folder:

```bash
docker compose stop julos                         # the server must not run during a restore
docker compose up -d postgres                     # an empty database is enough, the restore creates the schema
docker compose run --rm julos backup restore /backup/julos-db-20261119T030000Z.sql.gz --key /backup/secret.key
docker compose up -d julos
```

- `--key FILE` copies the key into `/data/secret.key` (mode 0600). Without it the key already there is used. A different key already in place is only replaced with `--force`.
  If `JULOS_SECRET_KEY` is set it wins over the file: put the old key there instead.
- The database must be empty (a fresh PostgreSQL volume). With `--force` an existing database is replaced completely. It is all or nothing: a damaged or cut-off dump changes nothing.
- At the end the command tries to decrypt every stored password with the key and says how many work. If it says some do not (exit code 3), the key is the wrong one.
- Without the command: `gunzip -c julos-db-....sql.gz | psql ...` also works once the server has created the schema (start it once against the empty database), but it does not check anything.

Then sign in as before (same password and authenticator): accounts, sessions, connections and stored passwords are back. Agents keep their tokens and reconnect.

## Check that it works

`deploy/dev/test.sh` runs `BackupTests`: dump, a fresh database, restore, login with password and authenticator code, and decrypting a stored password with the backed-up key.
Do the same by hand once on a spare PostgreSQL before you need it. A backup nobody has restored is only a hope.
