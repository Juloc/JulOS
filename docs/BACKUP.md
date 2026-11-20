# Backup and restore

JulOS keeps accounts, connections and stored target passwords in PostgreSQL. Stored passwords are encrypted (AES-256-GCM) with the key in
`/data/secret.key` (or `JULOS_SECRET_KEY`). **Lose either one and the stored passwords are gone**; lose the database and so are accounts and connections.
So JulOS backs up the database every day into a folder you choose. **Since 0.18 the key is not copied next to the dump any more** (a stolen backup alone must not open the passwords): you keep a **recovery key** instead, see "The recovery key". No extra container: the server does it itself.

## What is written

Into the backup folder (inside the container `/backup`), as **separate files**:

| File | What | When |
| --- | --- | --- |
| `julos-db-20261119T030000Z.sql.gz` | all tables, compressed text (UTC time in the name) | daily |
| `secret.key.wrapped` | the key, **wrapped** with your recovery key (useless without it) | when new or changed, once a recovery key is confirmed |
| `secret.key` | plain copy of `/data/secret.key` | **only** if you chose it (see "Settings") or an older version left one |

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
| `JULOS_BACKUP_KEY` | empty | Only the fallback; the setting "Put the key in the backup" in the app (Settings, Security) wins. `copy`: put a plain copy of `secret.key` next to the dump (**weak**: whoever reads the backup reads every stored password). `off`: never. Empty and nothing set in the app: **no plain copy**, except when the backup folder already holds one from an older version (it keeps being refreshed, so nothing is lost on update; the page says "so it was before", and switching it off in the app removes it). |

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

What is copied: the new dump(s) and, if one exists, the wrapped key `secret.key.wrapped` (and a plain `secret.key` only where it is switched on), as the same **separate files** as locally (dump and key are never packed together). A file is written under a
temporary name (`name.tmp`) and renamed at the end, so a cut connection never leaves a file at the target that only looks complete. Retention is the same as locally
(`JULOS_BACKUP_KEEP_DAYS`, the newest dump at the target is never deleted, files that are not JulOS dumps are never touched). The target folder is never created by the server.

A problem at the second target never damages the local backup: it is a warning in the log and in "Über JulOS" ("Zweites Ziel: Fehler ..."), and the next run
copies whatever is missing (also the dumps of the days in between, as long as they are inside the retention time). Without a plain key copy nothing but the wrapped file leaves the machine; switching the copy off in the app also removes an old copy at the second target.

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

Make the target **yours alone**: the dump holds password hashes, sealed authenticator secrets and the encrypted target passwords. The folder should belong to the user
JulOS writes as and have mode **0700**; JulOS creates the files with **0600** (over SFTP it asks the server for 0600 after the upload; a server that refuses keeps its default, so rely on the folder mode; on a share that does not know Unix modes, set the same restriction on the share: only your account may read it).
For SFTP use an account of its own that can reach only this folder (a chroot or a plain user with a home folder nobody else can read) and a key that is used for nothing else.
Keep the plain key copy off (the default) for a place others can read.

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

The files at the target are the same as in the local folder. Fetch the dump you want and `secret.key.wrapped` (`scp`, `sftp`, or copy from the share) into a folder the container can read (the wrapped file must lie **next to the dump**), then restore with the recovery key from your password manager:

```bash
mkdir -m 700 ~/restore && sftp julos@nas.example.org:julos-backup/julos-db-20261119T030000Z.sql.gz ~/restore/
sftp julos@nas.example.org:julos-backup/secret.key.wrapped ~/restore/
docker compose stop julos
docker compose up -d postgres
docker compose run --rm -it -v ~/restore:/restore:ro julos backup restore /restore/julos-db-20261119T030000Z.sql.gz --recovery-key
docker compose up -d julos
rm -r ~/restore                                                          # the copy of dump and key has done its job
```

For a share mounted at `/mnt/nas/julos-backup` just use `-v /mnt/nas/julos-backup:/restore:ro`. If the whole machine is gone: install JulOS on a new machine, get the files from the second target as above,
and restore before the first start of the server. The host key remembered at the target is not part of the backup: on the new machine JulOS remembers it again at first use.

## The recovery key

The key in `/data/secret.key` (or `JULOS_SECRET_KEY`) opens the stored target passwords **and** the authenticator secrets in the database. It lives only on the server. To be able to restore after the server is gone you keep a **recovery key**:

1. Settings, Security, "Create recovery key". The page shows it **once**, as 52 characters in groups of four (copy, or download as a file). The server does not keep it.
2. Put it in your password manager (not next to the backup), then press "I have saved it". Only then does it count; until then nothing changes. A new one can be created any time; the old one stays valid until the new one is confirmed.
3. From the next backup on, the folder (and the second target) holds `secret.key.wrapped`: the key wrapped with the recovery key (AES-256-GCM, key by HKDF-SHA256 from 256 random bits). It is useless without the recovery key, so it may lie next to the dump.

Restoring needs: the dump, `secret.key.wrapped` next to it, and the recovery key (see "Restore"). Without a recovery key and without a plain key copy, **a lost server also loses the stored passwords and the authenticator** (`tools/julos-user.sh` makes a new authenticator, passwords are typed again). Try it once on a spare database.

**Plain key copy (not recommended):** the setting "Put the key in the backup" (or `JULOS_BACKUP_KEY=copy`) writes `secret.key` next to the dump again. Whoever can read the backup can then open every stored password. Only for a folder that is readable by you alone. An installation that already had such a copy keeps it until you switch it off in the app; switching off removes the old copies in the folder and at the second target.

## The dump

- The dump contains **password hashes, sealed authenticator secrets and recovery code hashes**; it is as sensitive as the database. The backup folder must belong to you alone (mode 0700, files 0600, which the server sets).
- Stored target passwords and authenticator secrets are in the dump only in encrypted form (AES-256-GCM, 0.18 for the authenticator secrets), and the key is a **separate thing**, never inside the dump and by default not in the folder. A stolen dump alone shows no target password and no authenticator secret.
- With `JULOS_SECRET_KEY` in `.env` there is no `secret.key` file; the recovery key still wraps it, and `.env` is yours to back up.

## Restore

Pick the dump and make sure you have the recovery key (and `secret.key.wrapped` next to the dump) or a matching `secret.key`. On the Docker host, in the stack folder:

```bash
docker compose stop julos                         # the server must not run during a restore
docker compose up -d postgres                     # an empty database is enough, the restore creates the schema
docker compose run --rm -it julos backup restore /backup/julos-db-20261119T030000Z.sql.gz --recovery-key
docker compose up -d julos
```

- `--recovery-key` asks for the recovery key (input hidden; or `--recovery-key-file FILE` with the text in it), unwraps `secret.key.wrapped` from the folder of the dump and installs the key into `/data/secret.key` (mode 0600). Without any key in place and in a terminal the command asks by itself when the wrapped file is there. A wrong recovery key changes nothing (it is checked before the database is touched).
- `--key FILE` copies a plain key file into `/data/secret.key` (mode 0600), for backups that have one. Without it the key already there is used. A different key already in place is only replaced with `--force`.
  If `JULOS_SECRET_KEY` is set it wins over the file: put the old key there instead.
- The database must be empty (a fresh PostgreSQL volume). With `--force` an existing database is replaced completely. It is all or nothing: a damaged or cut-off dump changes nothing.
- At the end the command tries to decrypt every stored password and every authenticator secret with the key and says how many work. If it says some do not (exit code 3), the key is the wrong one.
- Without the command: `gunzip -c julos-db-....sql.gz | psql ...` also works once the server has created the schema (start it once against the empty database), but it does not check anything.

Then sign in as before (same password and authenticator): accounts, sessions, connections and stored passwords are back. Agents keep their tokens and reconnect.

## Check that it works

`deploy/dev/test.sh` runs `BackupTests`: dump, a fresh database, restore, login with password and authenticator code, and decrypting a stored password with the backed-up key. `BackupKeyTests` covers the recovery key: no key in the folder by default, the wrapped file, restore with the recovery key (and a wrong one changes nothing), an old key copy kept until switched off, and the sealed authenticator secret.
`BackupCopyTests` covers the second target: folder copy (atomic, retention, failure and catch-up) and SFTP against the throw-away SSH target of the test stack (password and key login, host key remembered and checked).
Do the same by hand once on a spare PostgreSQL before you need it. A backup nobody has restored is only a hope.

## The server may not write into the backup folder

Status "Fehlgeschlagen: In den Backup-Ordner /backup darf der Server nicht schreiben" means the folder is owned by someone else than the server user. With the published image
the folder `/backup` exists in the image for the server user (since 0.10.3), so a new or **empty** named volume takes that owner at its first start. A volume that already
holds files from `root` (or a bind-mounted host folder) needs the owner once, from the host:

```sh
docker run --rm -v <volume or host folder>:/backup --user 0:0 --entrypoint chown <image> <server uid>:<server gid> /backup
```

(`<server uid>` is `APP_UID` of the image, 1654 for the standard .NET images; the compose files of this repository use `JULOS_UID`/`JULOS_GID` instead.) The status turns green after the next run at the configured time.
