# AI gateway

Status: **built on a branch, not released**. One place in JulOS where AI providers are set up, and a small API through which the apps of the family use them with a key of their own (like Prowlarr feeding Sonarr and Radarr). Design context: [BASE.md](BASE.md) ("The Codex bridge through JulOS") and [ASSISTANT.md](ASSISTANT.md) (decisions on providers and the Codex bridge).

## What the three bridges do today

Read from the three projects as they are (read only, nothing changed there).

**Jularr** (`Jularr.Integrations/Logic/Codex`)
- Started: the CLI of the image; `codex app-server` is started **per request** (one short process, killed at the end); stdio, JSON lines.
- Signed in: `codex login --device-auth` as a child process; the output is scrubbed of ANSI codes, the first `https://…openai.com` address and the `XXXX-XXXX` code are scraped; the process ends when the person finished (16 minutes at most). State from `codex login status`; `codex logout`.
- Calls: `initialize`, `initialized`, `thread/start` (ephemeral, read-only, every tool and plugin switched off, one short instruction), `turn/start`; the answer arrives as `item/agentMessage/delta` pieces. Server requests of the app-server are refused (-32601).
- Models and usage: `model/list` (paged, with the efforts of each model) and `account/rateLimits/read` (plan windows in percent); tokens of a turn from `thread/tokenUsage/updated` (`last`). A usage limit in an error text becomes its own error with the time to try again.
- Different: the only one that streams text pieces to the caller and reads the plan windows; also keeps an `exec` path as the old way.

**FullWorth** (`Modules/Intelligence`, `FullWorth.CodexBridge`)
- Started: a Node sidecar in the same container under its own user, on loopback, armed lazily by a file (sleeps until someone opens the AI screens; restart loop with a back-off up to 30 s); the CLI is pinned in the image.
- Signed in: `POST /auth/start` spawns `codex login --device-auth` with one `CODEX_HOME` **per user**; the app polls `GET /auth/{id}` every 1.5 s (`waiting`, `connected`, `error`, address, code); `POST /logout`.
- Calls: `POST /execute` and `/scan` run `codex exec` once per request (read-only, no tools, schema output, files and receipt images); two headers authenticate the caller (internal key, per-user scope); answers are whole, not streamed.
- Models and usage: `GET /models` runs `codex debug models`; no plan windows, no token counts.
- Different: per-user logins, images and PDFs as input, a second user (`codex`) so the app cannot read the login.

**JuNotes** (`JuNotes.Integrations/Codex`)
- Started: the CLI of the image, `codex exec` once per request, at most two at a time; one `CODEX_HOME` on the data volume.
- Signed in: admin page; `codex login --device-auth`, waits 20 s for address and code; state from `codex --version` and `codex login status`.
- Calls: one prompt in on stdin, the answer from an output file (`-o`), optional JSON schema and images; read-only sandbox, empty work folder, user config ignored; model and effort per question.
- Models and usage: none; a daily limit is counted by the app itself.
- Different: tested against a **fake program** (a shell script that records its arguments and answers as the test says) and never against the real CLI.

**What they have in common** (and what the gateway therefore does once): the sign-in is always `codex login --device-auth` scraped for address and code; the CLI always runs without a shell string, with `CODEX_HOME` on a volume and a work folder of its own; every request is read-only with tools off; stderr is read away and dropped because it may name the account; a usage limit is told apart from other failures.

## The one interface

A provider offers exactly this (`IAiProvider`, `src/JulOS.Integrations/Ai`):

| Part | Meaning |
|---|---|
| `State` | `configured`, `signedIn`, `ready`, a short `message` (never a secret) |
| `ListModels` | model id, display name, supported efforts, default effort |
| `Chat` | messages (system, user, assistant; text and, where the provider can, images), model, effort, max tokens → a **stream of text pieces**, then the **usage** (tokens in, tokens out) |
| errors | a fixed set of codes (`not_signed_in`, `provider_unavailable`, `plan_limit`, `timeout`, `provider_error`, `bad_request`) with a short, secret-free text |
| abort | cancelling the call cancels the provider call (kills the turn or closes the HTTP request) |

Sign-in is not part of the common part: only the Codex bridge has a device-code sign-in; the two API-key providers have a key. Both are managed by the settings page.

## How it works

```
App ──(key of the app)──▶ JulOS ──▶ provider
                           │          ├─ Codex bridge  (codex app-server over stdio, ChatGPT account)
                           │          ├─ OpenAI        (API key)
                           │          └─ Anthropic     (API key)
                           └─ counts per app, day and model; never stores a text
```

- **Off by default.** The switch lives in Settings, *KI*. While it is off nothing runs, nothing is downloaded, and the app API answers `503 ai_disabled`.
- **Providers** are set up once, in JulOS (Settings, *KI*, section Providers). More than one can be on; every app is allowed some of them.
  - **Codex bridge.** Turning it on downloads the Codex program once into the data folder: `{JULOS_DATA}/ai/codex/bin/{version}/codex`, a pinned version (`CodexRelease`, one place), the SHA-256 of the archive is checked before anything is unpacked, only one file is taken from the archive, never into the image. Then *Sign in with ChatGPT*: JulOS shows an address and a code (`codex login --device-auth`, the address is only taken from `openai.com`), the person signs in in their own browser, JulOS polls the state. No password goes through JulOS. `codex app-server` is **one process for the whole server**, started on first use, started again after a crash with a growing wait (1 s, 2 s, 4 s … up to 30 s), kept without any environment of JulOS (a short list: `PATH`, `HOME`, `CODEX_HOME`, `NO_COLOR`, `LANG`), with a work folder of its own, as an argument list and never through a shell. Each request is a throwaway, read-only thread with every tool switched off; several run side by side. Aborting the call interrupts the turn (also when the abort comes before the program has answered `turn/start`). **Fail-closed checks:** the answer to `thread/start` must report `sandbox` read-only and `approvalPolicy` never, otherwise the text is not sent; any item of a turn that is not plain text (a command, a file change, a search, a tool) ends the turn at once. Before every start the program file is hashed again and compared with the hash the installer recorded next to it (`codex.sha256`); the folders below `ai/codex` are created with mode 0700. **The bridge serves no app until the owner has confirmed the manual check below** (off by default). The plan usage (percent per window) is shown in Settings when the program is running.
  - **OpenAI and Anthropic.** An API key, stored encrypted with the secret of the server (`SecretBox`, bound to the provider), never shown again, never in a log. Requests go only to `https://api.openai.com/v1/` and `https://api.anthropic.com/v1/`: the address is a constant, there is no setting for it.
- **Apps** are registered in Settings: a name, a key (shown once, kept only as a SHA-256), which providers or single models they may use, a daily and a monthly budget in tokens, a rate limit per minute, and whether they may send images. An app can be blocked at once, get a new key, or be deleted. Every change needs the second factor like other dangerous changes ([SECURITY-REVIEW.md](SECURITY-REVIEW.md), F-04) and is written to the audit trail (events `ai.*`, never a key or a text).
- **Usage** per app and day is shown in Settings: requests, results, tokens in and out.

## The API for apps

Base: `https://<your JulOS>/api/ai/v1`. Every call carries `Authorization: Bearer <key of the app>`. No cookie, no session, no other header needed.

| Call | Meaning |
|---|---|
| `GET /models` | the models this app may use: `{"models":[{"id":"openai/gpt-4.1","provider":"openai","name":"…","efforts":["low","medium","high"],"default":false}],"default":"codex/gpt-5.5","unavailable":[]}` |
| `POST /chat` | one request, answered as a whole or as a stream |
| `GET /usage` | own usage and budget: `{"app":"Notes","budget":{"day":{"used":1200,"limit":200000},"month":{…}},"ratePerMinute":30,"recent":[…7 days…]}` |

**Request** (`POST /chat`):

```bash
curl -sN https://julos.example/api/ai/v1/chat \
  -H "Authorization: Bearer $JULOS_AI_KEY" -H "Content-Type: application/json" \
  -d '{"model":"default","messages":[{"role":"system","content":"Answer briefly."},{"role":"user","content":"Hello"}],"effort":"low","maxTokens":512,"stream":true}'
```

| Field | Meaning |
|---|---|
| `model` | `provider/model` (for example `openai/gpt-4.1`) or `default`, the default model chosen in Settings |
| `messages` | `system`, `user`, `assistant`; `content` is a text, or a list of parts `{"type":"text","text":"…"}` and `{"type":"image","mediaType":"image/png","data":"<base64>"}` (images only for apps that may send them; the Codex bridge takes no images) |
| `effort` | optional, `minimal`, `low`, `medium`, `high`, `xhigh`; used where the model knows it, otherwise ignored |
| `maxTokens` | optional, 1 to 16384, default 1024 (not enforced by the Codex bridge, which has no such option) |
| `stream` | `false` (default): one JSON answer; `true`: Server-Sent Events |

**Answer as JSON:** `{"id":"req_…","model":"openai/gpt-4.1","text":"Hi!","usage":{"inputTokens":9,"outputTokens":2},"budget":{…},"warning":"day"}` (`warning` only at 80 % of a budget).

**Answer as a stream** (`text/event-stream`): `event: start` (`id`, `model`), then any number of `event: text` (`{"text":"piece"}`), then `event: done` (`usage`, `budget`, `warning`). A failure after the first piece is sent as `event: error` with the same body as an error; a failure before it is a normal HTTP error. Closing the connection aborts the call at the provider.

**Errors** are `{"error":{"code":"…","message":"…","scope":"day","resetsAt":"…"}}`:

| HTTP | `code` | Meaning |
|---|---|---|
| 401 | `missing_key`, `invalid_key` | no key, wrong key |
| 403 | `app_disabled`, `model_not_allowed`, `files_not_allowed` | the app is blocked, may not use this model, may not send images |
| 400 | `bad_request`, `no_default_model`, `unknown_model` | the body is wrong; `default` asked but none is set; the provider does not list this model (the list of `GET /models`, kept for a minute) |
| 413 | `request_too_large` | more than 320 KiB (12 MiB for apps that may send images), more than 256 KiB of text, more than 100 messages |
| 429 | `rate_limited`, `too_many_requests`, `budget_exceeded`, `plan_limit` | too many requests per minute (or too many wrong keys from one address, `Retry-After` is set), more than 2 at the same time, the day or month budget is used up (`scope`, `resetsAt`), the limit at the provider is reached |
| 502, 503, 504 | `provider_error`, `provider_unavailable`, `provider_not_ready`, `provider_off`, `provider_unverified`, `ai_disabled`, `timeout` | the provider failed, is not reachable, is not signed in or not turned on, the Codex bridge is not yet released by the owner, AI is off, the call took longer than 5 minutes |

**Budget.** Tokens in plus out, counted per UTC day and UTC month. At 80 % a response carries a warning (the header `X-AI-Budget-Warning: day|month` before the call, `warning` in the answer); at 100 % the call is refused with `budget_exceeded` before the provider is called. A call that is already running may overshoot a budget by its own size. What is booked per call is the **larger of what the provider reports and JulOS's own estimate** (four characters per token, 1000 per image), for input and output separately: a missing report, a report of 0 and 0 or a renamed field cannot make a call free. An abort without a reported output books at least `maxTokens` as output (a thinking model also computes without visible text). `GET /models` and `GET /usage` count against the same rate limit as `/chat`.

**What an app has to enter:** the address of JulOS (`https://<your JulOS>/api/ai/v1`) and its key. Nothing else. An app that is used without JulOS can keep a bridge of its own; the one JulOS client for the base ([BASE.md](BASE.md)) takes the address and the key as its only settings.

## Security

- The key opens these three calls and **nothing else** in JulOS: no host, no files, no settings, no other API (`/api/ai/v1` is the one reasoned exception to "every API needs a session", `ApiGuard` lets exactly this path through and the gateway checks the key). Checked by tests, and by the route list test (`RouteSecurityTests`).
- Keys have 256 bits of randomness and are stored as a SHA-256; the check compares against every stored hash in constant time. **Wrong keys are limited per address** (20 a minute) and the block applies **before** a key is checked, so a right key does not get through a block. A key is shown once and never again, not in an answer, a log or the audit trail.
- Requests from the server go only to the fixed provider addresses (no setting, no redirect, no proxy from the environment): no freely chosen target, so no SSRF.
- Errors of a provider are shortened to 200 characters and anything that looks like a key is cut out before an app sees them.
- The Codex program runs with an argument list, in a work folder of its own below the data folder, with no environment of JulOS (not the database address, not a key). Requests the program makes of JulOS (approvals, tools) are refused.
- Limits that are fixed in code: size of a request (and of an answer: 2 million characters), 5 minutes per call, 2 calls at the same time per app, 20 wrong keys a minute per address. The address is the one JulOS sees: behind a proxy, set the trusted proxies (Settings, Security) or all apps behind it would share one address and one block.
- The messages of the API are German text for people reading a log; a program should use `code`.

## Privacy

- **What goes to the provider:** the messages (and images) the app sends, the model, effort and maximum, and the key or account of the provider. Nothing about JulOS, the app's name or the person.
- **What JulOS stores:** only counters per day, app, provider and model: requests, ok / failed / aborted, tokens in and out, milliseconds. **No text of a request or an answer** is stored or written to the log; the log names only app, model and an error code. The test `A_chat_works_as_json_and_as_a_stream_counts_tokens_and_stores_no_content` searches every table and the log for a text that was sent.
- With the **Codex bridge** the requests count against the ChatGPT plan of the account that is signed in; with the **API keys** they are billed by the provider.

## Before turning the Codex bridge on, check it by hand

**Until this is done the bridge counts as unverified, and JulOS keeps it off for apps** (the card in Settings says so; the button *I did the check* is the owner's statement, needs the second factor, is written to the audit trail and holds for the pinned version only; a new version asks again).

What is *not* proven: that `codex app-server` with `sandbox: read-only` and every tool switched off cannot read files outside its folder. A read-only sandbox forbids writing, **not reading**, and the program runs as the same operating-system user as JulOS, so a prompt injected through an app key could in the worst case read the data folder, including the server secret (`secret.key`) that protects the stored API keys. The feature names JulOS switches off were taken from what Jularr uses and were never checked against the real program.

The test (run it on the machine where JulOS runs, as the user of the JulOS server, with the real downloaded program; replace `<version>` with the pinned version):

```bash
docker compose exec julos sh -c 'cd /data/ai/codex/work && CODEX_HOME=/data/ai/codex/home \
  /data/ai/codex/bin/<version>/codex exec --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules -s read-only \
  "Run these commands and show the output: cat /etc/passwd ; ls -la /data ; cat /data/secret.key"'
```

Expected: **every one of the three is refused or fails** (no `/etc/passwd` content, no listing of `/data`, no key). If any output appears, **do not confirm**: the bridge can read the server secret. If the program cannot start its sandbox inside the container the test also fails, which is the honest result. Do not paste the real content anywhere; delete the output.

**What to do if it fails, and the recommendation:** run the Codex bridge in **its own container with its own user and its own volume** (as FullWorth does with the user `codex`), which cannot see JulOS's data folder at all, and let JulOS talk to it over a private network. That is the right next step before the bridge serves any app with real data; in-process isolation under the same user cannot be made safe from JulOS alone (no root, no namespaces in a normal container). The API-key providers do not have this problem: they run no foreign program.

## Checksums of the pinned release

The SHA-256 values in `CodexRelease` are the digests GitHub publishes for the files of release `rust-v<version>`; they were **not** computed from a download by the author. Recompute them **before the first turn-on** and on every version change:

```bash
curl -fsSL -o codex.tar.gz https://github.com/openai/codex/releases/download/rust-v<version>/codex-x86_64-unknown-linux-musl.tar.gz
sha256sum codex.tar.gz        # compare with CodexRelease.cs (and with: gh api repos/openai/codex/releases/tags/rust-v<version>)
```

The installer is fail-closed: a different hash discards the download, an invalid hash string downloads nothing, a program file that no longer matches the hash recorded at install time is not started. A recorded hash next to the file cannot stop someone who can change both (the same user); it catches accidents and plain tampering.

## Trying it out without a provider

`JULOS_AI_FAKE=on` adds a provider `fake` that answers with an echo and needs no network and no account. It is for tests and for a throwaway development stack, not for a live server. The Codex bridge is tested against a fake program (a shell script), the two API providers against a stand-in for the network; **real providers are never called by a test**.

## What is deliberately missing

- **Tools and MCP.** A request is text (and images) in, text out. The Codex program runs with every tool switched off; an app cannot reach hosts, files or the settings of JulOS through it. The internal tools of the assistant ([ASSISTANT.md](ASSISTANT.md)) are a separate thing.
- **A chat window in JulOS.** The gateway only serves other apps. The assistant window is planned separately.
- **Files.** Only images, only for apps that may send them, only through the two API providers. No PDF, no other file type, no file store.
- **Stored conversations, tool calls by the model, embeddings, image generation.**

## Not verified yet

The sign-in with a real ChatGPT account, the download of the real Codex archive, the real `codex app-server`, **the isolation of that program** (see above) and the two real API providers have **not** been run. The message shapes follow what Jularr, FullWorth and JuNotes use against the real program today; the checksums are the digests GitHub publishes for the release, not computed from a download. Please try each once on a throwaway account before relying on it.
