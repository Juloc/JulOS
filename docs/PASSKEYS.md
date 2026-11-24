# Passkeys

Status: **built** (not released). Decision of the owner: a passkey becomes the normal way to sign in; password with authenticator code stays a full way. Plan: the base repository, `docs/plans/ACCOUNTS.md`, sections 3 and 5.

A passkey is a key pair on the person's device. The private half never leaves it (or the person's credential manager); JulOS keeps the public half. Signing in is a signature over a fresh challenge, after the device has checked the person (fingerprint, face or device PIN). There is nothing to type, nothing to phish, and nothing in the database that signs in as the person.

## What you can do

- **Add** (Konto, section *Passkeys*; phone: Profil, desktop: Einstellungen, Konto): name it, the browser asks for the fingerprint, done. Up to **20** per person. Needs a fresh proof (step-up with the authenticator code or an existing passkey), **even when the step-up switch is off**.
- **Rename**, and **remove** (removing needs the fresh proof too). Removing a passkey also ends all **other** sessions that were started with a passkey: a stolen device must not stay signed in.
- **Sign in** without a user name: the login page has *Mit Passkey anmelden*. Where the browser can (conditional UI), the passkey is also offered in the user-name field (autofill). The result is a normal session of strength `passkey`.
- **Confirm actions** (step-up): where the page asks for the authenticator code before a dangerous action, the sheet also has *Stattdessen mit Passkey bestätigen* (user verification required, same time window as the code).
- A passkey sign-in counts as a **fresh proof** itself: it opens the step-up window like a sign-in with a code does.

## The flows

All three use the same pieces: the server makes the options with a random **32-byte challenge**, stores only its **SHA-256** (table `passkey_challenges`), the browser calls `navigator.credentials`, the server verifies the answer and **deletes** the challenge in the same statement that finds it (`delete ... returning`), so it works **once**, within **5 minutes**, also against two parallel requests. The verification itself (origin, RP-id hash, challenge, flags, signature, counter, CBOR, COSE keys) is done by the library, not by JulOS.

| Flow | Options | Checked on top of the library |
|---|---|---|
| **Register** | resident key **required**, user verification **required**, attestation **none**, `excludeCredentials` = the person's own passkeys | the challenge was issued **to this person and this session**; the credential id is new (also against other people's); at most 20; fresh proof first |
| **Sign in** | empty allow list (discoverable), user verification **required** | the user handle in the answer must belong to the owner of the credential id; the account is set up; the counter does not run backwards |
| **Step-up** | allow list = the person's own passkeys, user verification **required** | the challenge was issued to this session; the credential belongs to this person |

**Counter:** 0 is accepted (synced passkeys always report 0). A counter that is lower than or equal to the stored non-zero one rejects the sign-in, **keeps the stored counter** and writes the audit entry `passkey.clone_suspected`. A counter update only succeeds if the stored value is still the one that was checked, so one answer cannot be used twice in parallel.

## Where the address comes from (RP id and origin)

A passkey is bound to the **RP id**, the host name of JulOS. Both RP id and origin come from the same trusted source as the public address of the sign-in centre (`OidcIssuer`: the setting in the app, else `JULOS_PUBLIC_URL`), **never from the `Host` header** of a request. Without that address there are no passkeys (the page says so). Rules:

- RP id = the host name, origin = scheme, host and port (a path in the address does not count).
- An **IP address** cannot be an RP id (browsers refuse): then passkeys are off.
- `http://localhost` is allowed for development; every other host needs `https`.

**If you change the address**, existing passkeys stop working, because the browser will not offer a passkey of `old.example` on `new.example`. Nothing is deleted: the entries stay in the list (and work again if you change back), and **password with authenticator code keeps working**. Sign in that way and add new passkeys on the new address; remove the old ones afterwards. Changing only the *port* or the scheme changes the origin but not the RP id: the library would refuse the old origin, so treat it as a change as well. Plan the change before you rely on passkeys only (and passkey-only is not offered, see below).

## What is stored

Table `passkeys` (migration `027_passkeys.sql`): credential id, **public key**, signature counter, name, AAGUID (the kind of authenticator, to recognise it), the backup flags (*may be synced*, *is synced now*), created, last used. **Nothing else**: no attestation statement, no biometric data (it never leaves the device), no user handle (it is derived: the 16 bytes of the account id; it carries no name). `sessions.strength` (`password` or `passkey`) says how a session began. Deleting a person deletes their passkeys.

## A passkey you did not add

A passkey created with a stolen cookie inside an open step-up window would survive a password change, a recovery and "end all sessions". So it must not stay unnoticed:

- Adding and removing a passkey writes the history (`passkey.added`, `passkey.deleted`) **and sends a push** (if the person switched push on): the name of the passkey as title, "Neuer Passkey eingerichtet" / "Passkey entfernt" as text (`PasskeyService.NotifyAsync`, texts in `sw-push.js`). A failed push never blocks anything.
- After **changing the password**, **setting up a new authenticator**, **ending the other sessions** and after **signing in with a recovery code**, the page shows, when passkeys exist: "Du hast N Passkeys. Prüfe, ob alle deine sind." with a button to the list. After a recovery code it also offers **Alle Passkeys entfernen** (second tap to confirm). **Nothing is deleted automatically.**

## Losing a device

1. **Another passkey** on another device or in the same credential manager.
2. Else **password with authenticator code** (or a recovery code), then remove the lost passkey in the list. That ends the other passkey sessions.
3. Lost everything: the console tool `tools/julos-user.sh` resets password and authenticator (see the README).

A passkey that is synced by a platform account (iCloud Keychain, Google Password Manager, a password manager) is as safe as that account. The list shows *may be synced to other devices* or *this device only* for each one.

## Threats and answers

| Threat | Answer |
|---|---|
| Phishing: a fake site asks for the sign-in | The browser only uses the passkey for the RP id it was made for; the origin is checked by the library |
| Replay of an answer | Challenge works once and for 5 minutes, deleted when looked up; counter must not go backwards |
| Cloned authenticator | Counter regression rejects and writes `passkey.clone_suspected` (limit: synced passkeys report 0 and cannot be checked this way) |
| Stolen session cookie adds a passkey | Adding and removing need a fresh proof, even with the step-up switch off; the registration challenge is bound to that session |
| Using a passkey of person A as person B | The user handle must match the owner of the credential id; the credential id is unique across all people |
| Finding out who has an account | One error text for every failure of the sign-in; the same status and body whether the passkey, the person or the signature was wrong |
| Guessing / flooding | At most 20 option and verify requests per minute per address; failures count towards the address lockout like wrong passwords (same keys, same growing delay); at most 2000 open sign-in challenges |
| Locking the owner out | Failures count against the **address**, never the account: a stranger who knows a credential id cannot lock the person out |
| Weak verification | User verification is **required** for every flow; a device that only proves presence is refused |
| Stolen device | Remove the passkey: its sessions end |
| Library bug | One library (Fido2 4.x, MIT) behind our interface `IPasskeys`; JulOS adds the checks above and writes no crypto itself |

## Known limits (accepted)

- Failed passkey sign-ins count into the same `ip:` lockout as wrong passwords (one budget per address, by design).
- The step-up with a passkey also counts failures on the account key `acct:`, like the code route does.
- An unknown credential id is answered a little faster than a known one with a bad signature (no password hash is computed either way); the answer text is the same.
- Open sign-in challenges: at most 10 per address and 2000 in total; a new one **pushes out the oldest** of its address (or of the table) instead of being refused, so a flood cannot lock others out of the passkey sign-in.
- `register/finish` has no fresh-proof check of its own: its challenge only exists because `register/begin` passed the check, and it belongs to that session. (A "once" window, minutes 0, is used up by `begin`.)

## Not built, on purpose

- **No attestation.** JulOS accepts any authenticator that does user verification; it does not ask which make it is (privacy, and a home server has no reason to pick). The AAGUID is kept for the list only.
- **No passkey-only mode and no duty to use passkeys.** Password with authenticator code stays a full way and cannot be removed; so removing the last passkey is always allowed, and the rule "the last way to sign in cannot be removed" (`PasskeyRules.CanRemove`) only guards a future change. A switch to refuse passwords is an idea, not built.
- **No push for a sign-in from a new device.** It is an entry in the security history only (`passkey.login` with the detail *neues Gerät* when no session of that person has the same browser string). Adding and removing a passkey **are** announced, see below.
- **No remember-which-passkey per session** (besides the strength). Removing any passkey ends all other passkey sessions, not only the ones made with that key.
- No per-person switch to turn passkeys off, no hardware-key-only mode, no `largeBlob`/PRF extensions.

## `amr` in the ID token

A session that began with a passkey says `"amr": ["hwk", "user"]` in the ID token of the sign-in centre, a session that began with password and code says `["pwd", "otp"]` (RFC 8176 values). RFC 8176 has no value named "passkey": `hwk` (proof of possession of a hardware-secured key) and `user` (user presence test) are the closest and what a relying party can act on. Caveat: a synced passkey lives in a credential manager rather than in dedicated hardware, and `user` does not tell a fingerprint from a PIN. JulOS does **not** add `mfa`; the app should read `hwk` as "phishing-resistant sign-in with a device key". See [SIGN-IN.md](SIGN-IN.md).

## Interface (all behind a session except the first two)

| Route | Needs |
|---|---|
| `POST /api/auth/passkey/login/begin` | **no session** (the reasoned exception, like the password sign-in): only makes a challenge; rate limit, address lockout, cap of open challenges |
| `POST /api/auth/passkey/login/finish` | **no session**: the library verifies the answer; one error for everything; address lockout |
| `GET /api/auth/passkeys` | session: the list (never credential id or key) and whether passkeys work here |
| `POST /api/auth/passkeys/register/begin` | session + **fresh proof** |
| `POST /api/auth/passkeys/register/finish` | session; the challenge must be this session's |
| `PUT /api/auth/passkeys/{id}` | session: rename |
| `DELETE /api/auth/passkeys/{id}` | session + **fresh proof** |
| `POST /api/auth/step-up/passkey/begin`, `.../finish` | session; opens the step-up window like a code |

Both open routes are named in `RouteSecurityTests` with their reasons. Bodies are capped at 16 KiB.

## Algorithms

Offered and accepted: **ES256, EdDSA (Ed25519) and RS256** (Windows Hello with a TPM), in that order. Not the library default (which also has ES384/512, PS* and RS384/512) and never RS1.

## Library and native part

**Fido2 4.3.3** (MIT, exactly this version in `Directory.Packages.props`, with its parts `Fido2.Models`, `NSec.Cryptography`, `libsodium` and `System.Formats.Cbor` pinned too through `CentralPackageTransitivePinning`; 5.x is still a preview) with `Fido2.Models`, `Microsoft.IdentityModel.JsonWebTokens` (already used), `Microsoft.Extensions.Http`, `System.Formats.Cbor` and **NSec.Cryptography** (MIT), which loads **libsodium** (ISC, from the NuGet package `libsodium`, which carries it for linux x64/arm64, glibc and musl). `Fido2Passkeys.cs` is the only file that knows the library; `IPasskeys` is the interface. .NET itself has passkeys only inside ASP.NET Core Identity, which JulOS does not use.

## Tests

`tests/JulOS.Backend.Tests/PasskeyTests.cs` runs the whole flow against the real server and database with a **software authenticator** (`PasskeyKit.cs`, ES256: it builds `clientDataJSON` and `authenticatorData` itself and signs, without the library under test): register and sign in; wrong origin, wrong RP hash, missing user verification, missing presence, wrong signature, unknown credential, foreign user handle, missing user handle, foreign challenge (all the same answer, no session); replayed and expired challenge; challenge stored only as hash; counter 0 allowed, counter backwards rejected and audited; credential of another person; duplicate registration; registration without fresh proof; challenge of another session; 20 limit; rename and delete (fresh proof, other passkey sessions end); step-up with a passkey (wrong ones leave no window); rate limit; address lockout without account lockout; garbage bodies; site derivation; `amr`. `tests/frontend/passkeys.test.mjs` covers the browser logic (Base64url, converting the options, shaping the answers).

**Native library in the image:** the image built from the `Dockerfile` (Ubuntu 24.04, glibc) was started against the dev PostgreSQL; an Ed25519 passkey registered and signed in there with status 200, which verifies with NSec/libsodium (`/app/runtimes/linux-x64/native/libsodium.so`), so the library loads without extra packages. The restore in the Docker build works with the pinning. The published folder carries the natives of every platform (about 20 MB, unused); pruning is possible later.

**Seen running:** in the dev stack (the image built from the working folder, `JULOS_PUBLIC_URL=http://localhost:<port>`) a passkey with the EdDSA algorithm registered and signed in over HTTP, which loads libsodium through NSec in the runtime image (the Ubuntu-based `aspnet:10.0`: nothing has to be installed, the NuGet package carries `libsodium.so` for linux x64 and arm64). In the browser pane a virtual authenticator written in page JavaScript (WebCrypto ES256, replacing `navigator.credentials`; the pane has no real one) went through the real page: adding a passkey in Konto, the step-up sheet with *Stattdessen mit Passkey bestätigen* before removing one, and *Mit Passkey anmelden* on the login page; phone and desktop sizes, light and dark.

**Not verified:** a real authenticator (Touch ID, Face ID, Windows Hello, Android, a security key), synced passkeys across devices, the autofill (conditional UI) in a real browser, and the browser's own error cases. Those stay a manual check at release.
