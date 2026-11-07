---
description: Compact the session into TODO.md + a handoff snapshot, then free context
---

Compact the current session state durably into this repository's tracking
docs, so the session context can be safely dropped afterwards.

Current repo state (injected — do NOT re-run unless rows look stale):

!`git -C . log --oneline -8 2>/dev/null; echo '---'; git -C . status --short; echo '---'; ls src/Realtime dev 2>/dev/null`

Steps:

1. **Update `TODO.md`** — tick/untick the checklist items to reflect what
   actually landed in this session; add anything new under "Next up".
2. **Append a "Session log — <today's date>" section** to `TODO.md` (keep it
   under ~25 lines):
   - what was done (files + one-line each),
   - decisions taken (with the plan-section reference, e.g. plan §5.2),
   - open blockers with the exact next command to investigate,
   - how to resume: the exact commands (e.g. `dotnet run` incantation, or
     `dev/run-spike.sh`) and which background services it needs.
3. **Rules**: never copy secrets, tokens, `.env` values, or token-cache
   contents into the log — file paths only. Never paste raw server logs;
   summarize them. Keep the plan file (`docs/PLAN-OWN-SERVER.md` in the
   sibling centrifugo repo) as the architecture source of truth — do not
   duplicate design into TODO.md.
4. Finish by replying with: items ticked, blockers outstanding, and the
   one-line "next action" for a fresh session.

Context-light summary style only — this log is the next session's entry point.
