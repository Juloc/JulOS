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

If Docker creates it for you it belongs to root and the server cannot write: the log and the About window say so. Then get the backup off the machine: either let JulOS copy it to a second target itself (next section), or copy the folder (another disk, NAS, `rsync`, your usual backup tool): a backup on the same disk does not survive the disk.

## A second target off the machine (optional)

By default the backup folder lives on the same machine (in the Compose stacks a folder on the same disk). If that machine dies, the backup dies with it. So JulOS can
**copy every new backup to a second place outside the machine**, right after the local backup succeeded. It is **off by default**; the local backup works exactly as before without it.

What is copied: the new dump(s) and the key copy `secret.key`, as the same **separate files** as locally (dump and key are never packed together). A file is written under a
temporary name (`name.tmp`) and renamed at the end, so a cut connection never leaves a file at the target that only looks complete. Retention is the same as locally
(`JULOS_BACKUP_KEEP_DAYS`, the newest dump at the target is never deleted, files that are not JulOS dumps are never touched). The target folder is never created by the server.

A problem at the second target never damages the local backup: it is a warning in the log and in "Über JulOS" ("Zweites Ziel: Fehler ..."), and the next run
copies whatever is missing (also the dumps of the days in between, as long as they are inside the retention time). With `JULOS_BACKUP_KEY=off` the key is copied to neither place.

| Variable | Default | Meaning |
| --- | --- | --- |
| `JULOS_BACKUP_COPY` | `off` | `off`, `path` or `sftp`. Anything else stops the server at start with a clear message. |
| `JULOS_BACKUP_COPY_PATH` | | `path`: the additional folder **inside the container** (a mounted NAS share, a second disk). Must exist and be writable, and must not be the local backup folder. |
| `JULOS_BACKUP_SFTP_HOST` | | `sftp`: host name or address of the SFTP server. |
| `JULOS_BACKUP_SFTP_PORT` | `22` | |
| `JULOS_BACKUP_SFTP_USER` | | |
| `JULOS_BACKUP_SFTP_PATH` | | Folder on the server (must exist; it is never created). |
| `JULOS_BACKUP_SFTP_KEY_FILE` | | Path **inside the container** of the private key (OpenSSH or PEM format). Mount it read-only. Preferred. |
| `JULOS_BACKUP_SFTP_KEY_PASSPHRASE_FILE` | | Optional: file with the passphrase of that key. |
| `JULOS_BACKUP_SFTP_PASSWORD_FILE` | | Instead of a key: file with the password (one line). Exactly one of key file and password file. |
| `JULOS_BACKUP_SFTP_HOST_KEY` | | Optional: pin the server's host key (`SHA256:...` as `ssh-keygen -lf` shows it). Without it the key is remembered at first use, see below. |

There is deliberately **no password and no key in any environment variable**, only the paths of files, so they do not show up in `docker inspect`, `docker compose config` or process listings, and JulOS never writes them to the log.

### The SFTP host key

The first time JulOS connects it remembers the server's host key in `/data/backup-sftp-hostkey` (algorithm and SHA-256 fingerprint, the same format as `ssh-keygen -l`) and
logs a warning with the fingerprint so you can compare it once with the real one (`ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the target). From then on the key is checked on every
connection. **If it changes, nothing is transferred**: the log (as an error) and "Über JulOS" show both fingerprints. If the target was reinstalled on purpose, delete
`backup-sftp-hostkey` in the data volume (or change `JULOS_BACKUP_SFTP_HOST_KEY`) after checking, and the next run remembers the new key. The check is the same idea as in the JulOS terminal.

### Access rights

Make the target **yours alone**: the dump holds password hashes and authenticator secrets, and `secret.key` opens the stored target passwords. The folder should belong to the user
JulOS writes as and have mode **0700**; JulOS creates the files with **0600** (on a share that does not know Unix modes, set the same restriction on the share: only your account may read it).
For SFTP use an account of its own that can reach only this folder (a chroot or a plain user with a home folder nobody else can read) and a key that is used for nothing else.
The same advice as for the local folder applies: if the second target is a place others can read, use `JULOS_BACKUP_KEY=off` and keep `secret.key` separately.

### Example: a NAS mounted on the Docker host

Mount the share on the host first (fstab, systemd mount, or `mount -t cifs` / NFS), owned by the user the server runs as (`JULOS_UID`), mode 0700, for example `/mnt/nas/julos-backup`.
Then in `docker-compose.yml` / `docker-compose.image.yml`, service `julos`:

```yaml
    environment:
      JULOS_BACKUP_COPY: "path"
      JULOS_BACKUP_COPY_PATH: "/backup-copy"
    volumes:
      - /mnt/nas/julos-backup:/backup-copy
```

(or put `JULOS_BACKUP_COPY=path` and `JULOS_BACKUP_COPY_PATH=/backup-copy` into `.env` and only add the volume line.) If the share is not mounted when the container starts, Docker would create an empty folder
on the host's disk: mount the share before `docker compose up`, and check "Zweites Ziel" in "Über JulOS" after the first run (it says "Fehler" if the folder is missing or not writable).

### Example: SFTP

Create a key pair only for this (on any machine), put the public key into `~/.ssh/authorized_keys` of the account on the SFTP server, create the folder there, and keep the private key on the Docker host
with mode 0600:

```bash
ssh-keygen -t ed25519 -N "" -C julos-backup -f ~/julos-backup-key       # public key: ~/julos-backup-key.pub
ssh-copy-id -i ~/julos-backup-key.pub julos@nas.example.org              # or copy the line into authorized_keys by hand
ssh julos@nas.example.org mkdir -m 700 julos-backup                      # a relative path is relative to the account's home folder
```

In `.env` (names only, no secret):

```
JULOS_BACKUP_COPY=sftp
JULOS_BACKUP_SFTP_HOST=nas.example.org
JULOS_BACKUP_SFTP_USER=julos
JULOS_BACKUP_SFTP_PATH=julos-backup
JULOS_BACKUP_SFTP_KEY_FILE=/run/secrets/julos_backup_sftp_key
```

and in the Compose file, service `julos`, mount the key **read-only** (the file must be readable by the server user, `JULOS_UID`):

```yaml
    volumes:
      - ${HOME}/julos-backup-key:/run/secrets/julos-backup-sftp-key:ro
```

(set `JULOS_BACKUP_SFTP_KEY_FILE` to exactly the path after the colon). With a password instead: `JULOS_BACKUP_SFTP_PASSWORD_FILE` and a file that holds only the password, mounted the same way.
Test it with `docker compose exec julos dotnet JulOS.dll backup create`: it prints "Zweites Ziel ...: kopiert" or a warning with the reason.

### Restore from the second target

The files at the target are the same as in the local folder. Fetch the dump you want and `secret.key` (`scp`, `sftp`, or copy from the share) into a folder the container can read, then restore as above:

```bash
mkdir -m 700 ~/restore && sftp julos@nas.example.org:julos-backup/julos-db-20261119T030000Z.sql.gz ~/restore/
sftp julos@nas.example.org:julos-backup/secret.key ~/restore/
docker compose stop julos
docker compose up -d postgres
docker compose run --rm -v ~/restore:/restore:ro julos backup restore /restore/julos-db-20261119T030000Z.sql.gz --key /restore/secret.key
docker compose up -d julos
rm -r ~/restore                                                          # the copy of dump and key has done its job
```

For a share mounted at `/mnt/nas/julos-backup` just use `-v /mnt/nas/julos-backup:/restore:ro`. If the whole machine is gone: install JulOS on a new machine, get the files from the second target as above,
and restore before the first start of the server. The host key remembered at the target is not part of the backup: on the new machine JulOS remembers it again at first use.

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
`BackupCopyTests` covers the second target: folder copy (atomic, retention, failure and catch-up) and SFTP against the throw-away SSH target of the test stack (password and key login, host key remembered and checked).
Do the same by hand once on a spare PostgreSQL before you need it. A backup nobody has restored is only a hope.
