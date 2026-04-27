module BotSharp.Infrastructure.Skills.DefaultSkills

open System.IO

// ═══════════════════════════════════════════════════════════════════════════
// DefaultSkills — installs built-in skill SKILL.md files to the workspace
//
// Mirrors Python nanobot's bundled skills/ directory. Two skills are always-
// active and injected into the system prompt; they must be present in
// {workspace}/skills/ or the agent lacks their runtime guidance.
//
// Skills are installed only when the SKILL.md file is absent — user
// modifications to existing skill files are never overwritten.
// ═══════════════════════════════════════════════════════════════════════════

// ── Built-in skill definitions ────────────────────────────────────────────

let private memorySkill = """---
name: memory
description: Two-layer memory system with Dream-managed knowledge files.
activation: always
---

# Memory

## Structure

- `SOUL.md` — Bot personality and communication style. **Managed by Dream.** Do NOT edit.
- `USER.md` — User profile and preferences. **Managed by Dream.** Do NOT edit.
- `memory/MEMORY.md` — Long-term facts (project context, important events). **Managed by Dream.** Do NOT edit.
- `memory/history.jsonl` — append-only JSONL, not loaded into context. Prefer the built-in `grep` tool to search it.

## Search Past Events

`memory/history.jsonl` is JSONL format — each line is a JSON object with `cursor`, `timestamp`, `content`.

- For broad searches, start with `grep(..., path="memory", glob="*.jsonl", output_mode="count")` or the default `files_with_matches` mode before expanding to full content
- Use `output_mode="content"` plus `context_before` / `context_after` when you need the exact matching lines
- Use `fixed_strings=true` for literal timestamps or JSON fragments
- Use `head_limit` / `offset` to page through long histories
- Use `exec` only as a last-resort fallback when the built-in search cannot express what you need

Examples (replace `keyword`):
- `grep(pattern="keyword", path="memory/history.jsonl", case_insensitive=true)`
- `grep(pattern="2026-04-02 10:00", path="memory/history.jsonl", fixed_strings=true)`
- `grep(pattern="keyword", path="memory", glob="*.jsonl", output_mode="count", case_insensitive=true)`
- `grep(pattern="oauth|token", path="memory", glob="*.jsonl", output_mode="content", case_insensitive=true)`

## Important

- **Do NOT edit SOUL.md, USER.md, or MEMORY.md.** They are automatically managed by Dream.
- If you notice outdated information, it will be corrected when Dream runs next.
- Users can view Dream's activity with the `/dream-log` command.
"""

let private mySkill = """---
name: my
description: Check and set the agent's own runtime state (model, iterations, context window, token usage). Use when diagnosing why something doesn't work, checking resource limits before complex tasks, adapting configuration for long or simple tasks, or remembering user preferences across turns.
activation: always
---

# Self-Awareness

## How to use

1. **Identify the situation** from the categories below
2. **Call the my tool** with the appropriate action
3. **If set**, warn the user before changing impactful settings (model, iterations)

## When to check

<rule>
**Diagnose before explaining.** When something doesn't work, check your state first.
</rule>

<rule>
**Check budget before complex tasks.** Know your limits before committing.
</rule>

<rule>
**Recall across turns.** Store preferences in your scratchpad, read them back later.
</rule>

## When to set

<rule>
**Only set when benefit is clear and user is informed.** Warn before changing model.
</rule>

| Situation | Command |
|-----------|---------|
| Large codebase analysis | `my(action="set", key="context_window_tokens", value=131072)` |
| Repetitive simple tasks | `my(action="set", key="model", value="<fast-model>")` |
| Long multi-step task | `my(action="set", key="max_iterations", value=80)` |

**Tradeoff:** Bias toward stability. Only set when defaults are genuinely insufficient.

## Anti-patterns

<rule>
**Don't check every turn.** Costs a tool call. Use when you need information, not reflexively.
</rule>

<rule>
**Don't store sensitive data.** No API keys, passwords, or tokens in scratchpad.
</rule>

## Constraints

- All modifications in-memory only — restart resets everything
- Protected params have type/range validation: `max_iterations` (1–100), `context_window_tokens` (4096–1M), `model` (non-empty string)

## Related tools

| Need | Use | Persists? |
|------|-----|-----------|
| Per-session temp state | `my(action="set", key="...", value=...)` | No |
| Long-term facts | Memory skill (`MEMORY.md`, `USER.md`) | Yes |
| Permanent config change | Edit config file | Yes |

**Rule of thumb:** Tomorrow? Memory. This turn only? My.
"""

let private cronSkill = """---
name: cron
description: Schedule reminders and recurring tasks.
---

# Cron

Use the `cron` tool to schedule reminders or recurring tasks.

## Three Modes

1. **Reminder** - task is sent directly to user
2. **Task** - task is executed by the agent each time it fires
3. **One-time** - runs once at a specific time (use `at` parameter)

## Examples

Recurring task every 20 minutes:
```
cron(action="add", task="Time to take a break!", schedule="every 20m", channel="cli", chat="direct")
```

Dynamic agent task (agent executes each time):
```
cron(action="add", task="Check BotSharp GitHub stars and report", schedule="every 10m", channel="cli", chat="direct")
```

One-time scheduled task (compute ISO datetime from current time):
```
cron(action="add", task="Remind me about the meeting", at="2026-04-26T14:00:00Z", channel="cli", chat="direct")
```

Timezone-aware daily schedule:
```
cron(action="add", task="Morning standup", schedule="0 9 * * 1-5", tz="America/Vancouver", channel="cli", chat="direct")
```

List/remove/pause/resume/run:
```
cron(action="list")
cron(action="remove", job_id="abc123")
cron(action="pause", job_id="abc123")
cron(action="resume", job_id="abc123")
cron(action="run", job_id="abc123")
```

## Schedule Formats

| User says | schedule parameter |
|-----------|------------|
| every 20 minutes | "every 20m" |
| every hour | "every 60m" |
| every day at 8am | "daily at 08:00" |
| weekdays at 5pm | "0 17 * * 1-5" (cron expression) |
| weekly on Monday at 9am | "weekly Monday at 09:00" |
| at a specific time | use `at` param with ISO 8601 datetime |

## Timezone

Use `tz` with cron expressions and daily/weekly schedules to target a specific IANA timezone.
Without `tz`, UTC is used.
"""

let private githubSkill = """---
name: github
description: "Interact with GitHub using the `gh` CLI. Use `gh issue`, `gh pr`, `gh run`, and `gh api` for issues, PRs, CI runs, and advanced queries."
metadata: {"botsharp":{"emoji":"🐙","requires":{"bins":["gh"]}}}
---

# GitHub Skill

Use the `gh` CLI to interact with GitHub. Always specify `--repo owner/repo` when not in a git directory, or use URLs directly.

## Pull Requests

Check CI status on a PR:
```bash
gh pr checks 55 --repo owner/repo
```

List recent workflow runs:
```bash
gh run list --repo owner/repo --limit 10
```

View a run and see which steps failed:
```bash
gh run view <run-id> --repo owner/repo
```

View logs for failed steps only:
```bash
gh run view <run-id> --repo owner/repo --log-failed
```

## API for Advanced Queries

The `gh api` command is useful for accessing data not available through other subcommands.

Get PR with specific fields:
```bash
gh api repos/owner/repo/pulls/55 --jq '.title, .state, .user.login'
```

## JSON Output

Most commands support `--json` for structured output. You can use `--jq` to filter:

```bash
gh issue list --repo owner/repo --json number,title --jq '.[] | "\(.number): \(.title)"'
```
"""

let private tmuxSkill = """---
name: tmux
description: Remote-control tmux sessions for interactive CLIs by sending keystrokes and scraping pane output.
metadata: {"botsharp":{"emoji":"🧵","os":["darwin","linux"],"requires":{"bins":["tmux"]}}}
---

# tmux Skill

Use tmux only when you need an interactive TTY. Prefer exec background mode for long-running, non-interactive tasks.

## Quickstart (isolated socket, exec tool)

```bash
SOCKET_DIR="${BOTSHARP_TMUX_SOCKET_DIR:-${TMPDIR:-/tmp}/botsharp-tmux-sockets}"
mkdir -p "$SOCKET_DIR"
SOCKET="$SOCKET_DIR/botsharp.sock"
SESSION=botsharp-python

tmux -S "$SOCKET" new -d -s "$SESSION" -n shell
tmux -S "$SOCKET" send-keys -t "$SESSION":0.0 -- 'PYTHON_BASIC_REPL=1 python3 -q' Enter
tmux -S "$SOCKET" capture-pane -p -J -t "$SESSION":0.0 -S -200
```

After starting a session, always print monitor commands:

```
To monitor:
  tmux -S "$SOCKET" attach -t "$SESSION"
  tmux -S "$SOCKET" capture-pane -p -J -t "$SESSION":0.0 -S -200
```

## Socket convention

- Use `BOTSHARP_TMUX_SOCKET_DIR` environment variable.
- Default socket path: `"$BOTSHARP_TMUX_SOCKET_DIR/botsharp.sock"`.

## Targeting panes and naming

- Target format: `session:window.pane` (defaults to `:0.0`).
- Keep names short; avoid spaces.
- Inspect: `tmux -S "$SOCKET" list-sessions`, `tmux -S "$SOCKET" list-panes -a`.

## Sending input safely

- Prefer literal sends: `tmux -S "$SOCKET" send-keys -t target -l -- "$cmd"`.
- Control keys: `tmux -S "$SOCKET" send-keys -t target C-c`.

## Watching output

- Capture recent history: `tmux -S "$SOCKET" capture-pane -p -J -t target -S -200`.
- Attaching is OK; detach with `Ctrl+b d`.

## Spawning processes

- For python REPLs, set `PYTHON_BASIC_REPL=1` (non-basic REPL breaks send-keys flows).

## Cleanup

- Kill a session: `tmux -S "$SOCKET" kill-session -t "$SESSION"`.
- Remove everything on the private socket: `tmux -S "$SOCKET" kill-server`.
"""

let private weatherSkill = """---
name: weather
description: Get current weather and forecasts (no API key required).
homepage: https://wttr.in/:help
metadata: {"botsharp":{"emoji":"🌤️","requires":{"bins":["curl"]}}}
---

# Weather

Two free services, no API keys needed.

## wttr.in (primary)

Quick one-liner:
```bash
curl -s "wttr.in/London?format=3"
# Output: London: ⛅️ +8°C
```

Compact format:
```bash
curl -s "wttr.in/London?format=%l:+%c+%t+%h+%w"
# Output: London: ⛅️ +8°C 71% ↙5km/h
```

Full forecast:
```bash
curl -s "wttr.in/London?T"
```

Format codes: `%c` condition · `%t` temp · `%h` humidity · `%w` wind · `%l` location · `%m` moon

Tips:
- URL-encode spaces: `wttr.in/New+York`
- Airport codes: `wttr.in/JFK`
- Units: `?m` (metric) `?u` (USCS)
- Today only: `?1` · Current only: `?0`

## Open-Meteo (fallback, JSON)

Free, no key, good for programmatic use:
```bash
curl -s "https://api.open-meteo.com/v1/forecast?latitude=51.5&longitude=-0.12&current_weather=true"
```

Find coordinates for a city, then query. Returns JSON with temp, windspeed, weathercode.
"""

let private clawhubSkill = """---
name: clawhub
description: Search and install agent skills from ClawHub, the public skill registry.
homepage: https://clawhub.ai
metadata: {"botsharp":{"emoji":"🦞"}}
---

# ClawHub

Public skill registry for AI agents. Search by natural language (vector search).

## When to use

Use this skill when the user asks any of:
- "find a skill for …"
- "search for skills"
- "install a skill"
- "what skills are available?"
- "update my skills"

## Search

```bash
npx --yes clawhub@latest search "web scraping" --limit 5
```

## Install

```bash
npx --yes clawhub@latest install <slug> --workdir ~/.botsharp/workspace
```

Replace `<slug>` with the skill name from search results. This places the skill into `~/.botsharp/workspace/skills/`, where BotSharp loads workspace skills from. Always include `--workdir`.

## Update

```bash
npx --yes clawhub@latest update --all --workdir ~/.botsharp/workspace
```

## List installed

```bash
npx --yes clawhub@latest list --workdir ~/.botsharp/workspace
```

## Notes

- Requires Node.js (`npx` comes with it).
- No API key needed for search and install.
- `--workdir ~/.botsharp/workspace` is critical — without it, skills install to the current directory instead of the BotSharp workspace.
- After install, remind the user to start a new session to load the skill.
"""

let private summarizeSkill = """---
name: summarize
description: Summarize or extract text/transcripts from URLs, podcasts, and local files (great fallback for "transcribe this YouTube/video").
homepage: https://summarize.sh
metadata: {"botsharp":{"emoji":"🧾","requires":{"bins":["summarize"]}}}
---

# Summarize

Fast CLI to summarize URLs, local files, and YouTube links.

## When to use (trigger phrases)

Use this skill immediately when the user asks any of:
- "use summarize.sh"
- "what's this link/video about?"
- "summarize this URL/article"
- "transcribe this YouTube/video" (best-effort transcript extraction; no `yt-dlp` needed)

## Quick start

```bash
summarize "https://example.com" --model google/gemini-3-flash-preview
summarize "/path/to/file.pdf" --model google/gemini-3-flash-preview
summarize "https://youtu.be/dQw4w9WgXcQ" --youtube auto
```

## YouTube: summary vs transcript

Best-effort transcript (URLs only):

```bash
summarize "https://youtu.be/dQw4w9WgXcQ" --youtube auto --extract-only
```

If the user asked for a transcript but it's huge, return a tight summary first, then ask which section/time range to expand.
"""

let private skillCreatorSkill = """---
name: skill-creator
description: Create or update AgentSkills. Use when designing, structuring, or packaging skills with scripts, references, and assets.
---

# Skill Creator

Skills are modular, self-contained packages that extend the agent's capabilities. Think of them as "onboarding guides" for specific domains or tasks.

## What Skills Provide

1. Specialized workflows - Multi-step procedures for specific domains
2. Tool integrations - Instructions for working with specific file formats or APIs
3. Domain expertise - Company-specific knowledge, schemas, business logic
4. Bundled resources - Scripts, references, and assets for complex and repetitive tasks

## Core Principles

**Concise is Key.** The context window is shared. Only add context the agent doesn't already have. Challenge each piece: "Does the agent really need this?" Prefer concise examples over verbose explanations.

**Anatomy of a Skill:**
```
skill-name/
├── SKILL.md (required)
│   ├── YAML frontmatter: name, description
│   └── Markdown instructions
└── Optional resources/
    ├── scripts/      — Executable code for repeated tasks
    ├── references/   — Documentation loaded as needed
    └── assets/       — Files used in output (templates, icons)
```

## SKILL.md Guidelines

- `description`: Primary trigger mechanism — what the skill does AND when to use it. Include all "when to use" info here; the body only loads after triggering.
- Body: Instructions for using the skill. Keep under 500 lines to minimize context bloat.
- References: For detailed info, put it in `references/*.md` and link from SKILL.md. Agent loads these only when needed.

## Skill Creation Process

1. Understand the skill with concrete examples
2. Plan reusable contents (scripts, references, assets)
3. Create skill directory at `{workspace}/skills/{skill-name}/`
4. Write `SKILL.md` with frontmatter + instructions
5. Add any bundled resources
6. Test and iterate

## Naming

- Lowercase, digits, hyphens only
- Short, verb-led phrases (e.g., `gh-address-comments`, `linear-address-issue`)
- Folder name must match skill name exactly
"""

// ── Built-in skill registry ───────────────────────────────────────────────

/// (subdirectory-name, SKILL.md content) pairs for all built-in skills.
let private builtinSkills =
    [ "memory",        memorySkill
      "my",            mySkill
      "cron",          cronSkill
      "github",        githubSkill
      "tmux",          tmuxSkill
      "weather",       weatherSkill
      "clawhub",       clawhubSkill
      "summarize",     summarizeSkill
      "skill-creator", skillCreatorSkill ]

// ── Installation ──────────────────────────────────────────────────────────

/// Install built-in skills to {workspacePath}/skills/ if the SKILL.md file
/// is absent. Existing user-modified files are never overwritten.
/// Silently ignores I/O errors so a non-writable workspace doesn't crash startup.
let installDefaults (workspacePath: string) : unit =
    try
        let skillsRoot = Path.Combine(workspacePath, "skills")
        for (dirName, content) in builtinSkills do
            let dirPath  = Path.Combine(skillsRoot, dirName)
            let filePath = Path.Combine(dirPath, "SKILL.md")
            if not (File.Exists filePath) then
                try
                    Directory.CreateDirectory(dirPath) |> ignore
                    File.WriteAllText(filePath, content)
                with _ -> ()   // non-fatal — skill will simply be absent
    with _ -> ()   // ignore if skills root cannot be created
