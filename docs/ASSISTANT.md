# Assistant in JulOS (plan, not built)

Status: **planned**. Decisions from the owner's interview on 2026-11-20. Nothing is built yet; see "Phases".
The goal: an optional assistant inside JulOS that informs the owner about the hosts, helps, and proposes actions, with every write confirmed. Built like the Codex bridge of the owner's other project, but with JulOS tools (MCP) in front of the hosts.

## Decisions

- **Where it lives:** an own window (desktop) and page (phone) "Assistent": chat, model choice, reasoning effort, usage display. Not in the terminal, not in the explorer (later, maybe).
- **Switchable:** off by default; one switch in the settings turns it on. Nothing runs and nothing is downloaded while it is off.
- **Providers (first):** (1) **Codex bridge** (ChatGPT account, device-code sign-in in the JulOS UI, `codex app-server` over stdio as the other project does it; models, efforts and plan usage windows come from Codex itself), (2) OpenAI API key, (3) Anthropic API key. Several can be stored; the chat picks provider, model and effort per conversation. The provider interface stays open so another can be added.
- **Where Codex runs:** inside the JulOS server (as in the other project). The CLI is **downloaded when the switch is turned on** (pinned version with checksum, into a volume), not baked into the image.
- **Tools:** an **internal MCP server** in the JulOS server, used by the assistant only. Exposing it to the outside (Claude Code on a PC with a token) is later; it is the same server.
- **Data to the provider:** shortened, secrets redacted (passwords, tokens, keys), logs capped, only what the question needs. File contents leave the server only when the owner picked them.
- **Tokens:** small fast model as the default, high effort per message on request; a compact per-host overview as the base context instead of raw output; logs only on request and with a line limit; a daily/per-chat budget with a warning at 80 % and a stop at 100 %; the provider's plan usage shown in the chat where the provider gives it.
- **Knowledge ("base prompts"):** a notes field per host, editable global rules, quick actions with templates ("check updates on all hosts", "why is the disk full", "what is notable today"), and short cheat sheets from the JulOS docs.
- **Chats:** stored until the owner deletes them (no secrets stored), single and all deletable.
- **Alerts:** first **deterministic, without AI**: new security updates, disk almost full (threshold), service or container failed, agent offline or reboot needed; delivered as push over the PWA (Web Push). Optional AI-run scheduled tasks (like cron jobs that write a message) later.

## Command modes (chat setting, like other AI apps)

| Mode | What the assistant may do |
| --- | --- |
| **Read / plan** | Only read and propose. Nothing is executed. |
| **Ask** (default) | Every writing step waits for the owner's yes. |
| **Safe auto** | Read-only commands from a list run by themselves; everything else asks. The list starts small and checked (`df`, `free`, `uptime`, `ls`, `systemctl status`, `journalctl` with a line limit, `docker ps`); the owner may add or remove patterns. |
| **Time-boxed release** | The owner frees a host and a kind of command for e.g. 30 minutes; afterwards it falls back to Ask. |

Free commands run as the **normal agent user**; **root only one command at a time**, with a prominent notice and its own confirmation. A proposal waits **without a time limit** until the owner decides (owner's choice): to keep an old yes from doing something later, a proposal shows its age, is bound to the exact host and command text, and is re-checked against the host's current state before it runs; the shell command of a proposal older than one hour needs a second tap.

## Phases

1. **Read and use what exists:** the chat, the providers and the Codex bridge, the internal MCP tools over everything JulOS can already read (hosts, state, updates, logs, services, Docker, audit) and the existing confirmed host actions as proposals. Notes per host, global rules, quick actions, budget and usage display.
2. **Free commands** with the modes above, a new helper verb for a free command (fixed argument vector, no shell string composed by the model without confirmation), audit of every command.
3. **Files:** read and change files through the Files app tools, as root where allowed, with the confirmations of [FILES.md](FILES.md).

The deterministic alerts (push) are independent of the assistant and can be built first.

## Security notes

- The assistant is a client of the same tools and confirmations as the owner: the two keys (level of the host, word on the host), the server-issued confirmation, fail-closed audit and the pause switches apply unchanged. The assistant never confirms for the owner.
- A confirmation for a proposal is issued by the server after the owner taps; the model cannot call "run" itself.
- The owner's stored passwords and the server key are never part of any tool result or prompt.
- Free commands as root are the largest new power; they are phase 2 and need their own review.
