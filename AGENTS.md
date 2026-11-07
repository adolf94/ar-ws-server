# AI Agent Guidelines & Available Skills

This repository uses skills and conventions shared across all AI coding assistants (Antigravity, OpenCode, Claude Code, Cursor, Windsurf, Copilot).

---

## Available Skills

### 1. `doc-summarizer`
- **Location**: `.agents/skills/doc-summarizer/SKILL.md` (Antigravity) and `.opencode/agents/doc-summarizer.md` (OpenCode).
- **Purpose**: Rewrites, condenses, and optimizes documentation by removing redundancy, marketing language, and obsolete behavior while strictly preserving every technical fact, caveat, and rationale.

---

## Coding and Documentation Rules

1. **Documentation Integrity**: Maintain clear, concise documentation. Preserve rationale, warnings, and code signatures.
2. **Path References**: Use standard clean relative markdown links (e.g. `docs/CONTRACT.md`); never use `file://` URLs.
3. **Partition Isolation**: The `client_id` in access tokens is the physical partition key for all groups, members, and socket operations.
