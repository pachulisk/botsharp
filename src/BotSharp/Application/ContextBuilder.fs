module BotSharp.Application.ContextBuilder

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.AsyncResult
open BotSharp.Infrastructure.Skills.SkillsLoader

// ═══════════════════════════════════════════════════════════════════════════
// Context builder
//
// Builds the LLMRequest from a session snapshot + inbound message.
// Pure where possible; file I/O is limited to loading the system prompt.
//
// Memory injection modes:
//   - Progressive disclosure: when memory_summary.md exists or MEMORY.md
//     exceeds MemoryDirectInjectLimit, inject a summary + retrieval instructions.
//     Agent uses existing tools (read_file, grep) to retrieve details on demand.
//     Mirrors Codex's read_path.md prompt engineering pattern.
//   - Full injection: when MEMORY.md is small, inject the entire content
//     (backward compatible, optimal for short memory files).
// ═══════════════════════════════════════════════════════════════════════════

// ── Progressive memory disclosure template ──────────────────────────────
// Mirrors Codex memories/read/templates/memories/read_path.md.
// Agent is taught to search memory on-demand using existing file tools.

let private memoryReadPathTemplate (memoryBasePath: string) (sessionsPath: string) (memorySummary: string) : string =
    $"""# Memory System

You have access to a persistent memory system at `{memoryBasePath}`.

## When to Use Memory

Skip memory lookup ONLY when the task is completely self-contained (current time, trivial formatting, simple math).

Use memory by default when:
- Query mentions a workspace, project, repo, module, or path referenced in MEMORY_SUMMARY below
- User asks for prior context, consistency with previous decisions, or "what did we do last time"
- Task is ambiguous and could depend on earlier choices or user preferences
- Non-trivial work on topics covered in MEMORY_SUMMARY

## Memory Layout

```
{memoryBasePath}/
├── MEMORY.md            ← Searchable registry; PRIMARY FILE TO QUERY
├── HISTORY.md           ← Chronological session log
└── .dream_cursor        ← Internal (ignore)
```

## Retrieval Protocol (4-6 steps max)

1. **Skim MEMORY_SUMMARY** below — extract task-relevant keywords
2. **Search `MEMORY.md`** — `grep "keyword" {memoryBasePath}/MEMORY.md`
3. **If MEMORY.md points to files** — open 1-2 most relevant with read_file
4. **If unclear** — search session history for exact commands/errors:
   - `grep "error message" {sessionsPath}/<session>.jsonl`
5. **If no relevant hits** — STOP and continue normally

**Budget: ≤ 4-6 search steps before main work. Do NOT broadly scan all files.**

During execution: if repeated errors occur, redo a quick memory pass for similar past failures.

## Verification Strategy

When using facts from memory:
- **Easy to verify + might be stale** → verify before answering (e.g., file paths, versions)
- **Expensive to verify + might be stale** → answer from memory, note it may be outdated
- **Unlikely to change** → answer from memory directly (e.g., user preferences, decisions)

## Citation Format

When you use memory in your response, append one citation block at the END:

```
<mem-citation>
MEMORY.md:12-15|note=[user prefers dark theme]
</mem-citation>
```

Rules:
- One entry per line: `<file>:<line_start>-<line_end>|note=[brief usage description]`
- File paths relative to `{memoryBasePath}/`
- Order by importance (most important first)
- If no memory was used, omit the citation block entirely

---

## MEMORY_SUMMARY

{memorySummary}"""

// ── Token budget truncation ─────────────────────────────────────────────

/// Truncate text to a token budget (1 token ≈ 4 UTF-8 bytes).
/// Mirrors Codex read/src/prompts.rs TruncationPolicy::Tokens.
let private truncateToTokenBudget (maxTokens: int) (text: string) : string =
    let maxBytes = maxTokens * 4
    let textBytes = Text.Encoding.UTF8.GetByteCount(text)
    if textBytes <= maxBytes then text
    else
        // Truncate at UTF-8 character boundary
        let mutable byteCount = 0
        let mutable charCount = 0
        for c in text do
            let cb = Text.Encoding.UTF8.GetByteCount(string c)
            if byteCount + cb <= maxBytes then
                byteCount <- byteCount + cb
                charCount <- charCount + 1
        if charCount >= text.Length then text
        else text.[..charCount - 1] + "\n\n...(truncated)..."

let private readOptional (path: string) : Async<string option> =
    async {
        if File.Exists path then
            let! text = File.ReadAllTextAsync(path) |> Async.AwaitTask
            return Some (text.Trim())
        else
            return None
    }

/// Build a default identity block when IDENTITY.md is absent.
/// Includes workspace path so the agent knows where its files are.
let private defaultIdentity (workspacePath: string) : string =
    let os =
        if Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.OSX) then "macOS"
        elif Runtime.InteropServices.RuntimeInformation.IsOSPlatform(Runtime.InteropServices.OSPlatform.Linux) then "Linux"
        else "Windows"
    $"""# BotSharp

You are BotSharp, a helpful AI assistant.

## Runtime
{os} (.NET {Environment.Version})

## Workspace
Your workspace is at: {workspacePath}
- Long-term memory: {workspacePath}/memory/MEMORY.md
- History log: {workspacePath}/memory/HISTORY.md
- Custom skills: {workspacePath}/skills/{{skill-name}}/SKILL.md

## Guidelines
- State intent before tool calls, but never predict results before receiving them.
- Before modifying a file, read it first. Do not assume files exist.
- If a tool call fails, analyse the error before retrying.
- Ask for clarification when the request is ambiguous.
- When you create a file the user needs (e.g., via write_file), send it using the 'message' tool with the 'media' parameter containing the absolute file path.

Reply directly with text for conversations. Use the 'message' tool to send to a specific chat channel or to deliver file attachments via the 'media' parameter."""

/// Channel-specific formatting guidance injected into the system prompt.
/// Mirrors Python's identity.md Jinja template conditional for channel.
/// Returns None for cli, unknown channels, or when channel is None.
let private channelFormatHint (channelOpt: string option) : string option =
    match channelOpt with
    | Some ch when ch = "telegram" || ch = "qq" || ch = "discord" ->
        Some "## Format Hint\nThis conversation is on a messaging app. Use short paragraphs. Avoid large headings (#, ##). Use **bold** sparingly. No tables — use plain lists."
    | Some ch when ch = "whatsapp" || ch = "sms" ->
        Some "## Format Hint\nThis conversation is on a text messaging platform that does not render markdown. Use plain text only."
    | Some ch when ch = "email" ->
        Some "## Format Hint\nThis conversation is via email. Structure with clear sections. Markdown may not render — keep formatting simple."
    | _ -> None

/// Load the system prompt from the workspace.
///
/// `disabledSkills` — skill names to exclude from the loaded list (case-sensitive,
/// matches the skill's `name` field from frontmatter; mirrors Python's disabled_skills).
///
/// `channel` — the inbound channel name (e.g. "telegram", "whatsapp"). When Some,
/// a channel-specific Format Hint section is appended to the identity block.
/// Mirrors Python's `identity.md` Jinja template channel conditional.
///
/// Structure (sections joined with "---" separators):
///   1. IDENTITY.md  — agent persona / identity (or built-in fallback)
///   2. [Format Hint] — channel-specific formatting guidance (optional)
///   3. Bootstrap files each formatted as "## {filename}\n\n{content}":
///      AGENTS.md, SOUL.md, USER.md, TOOLS.md (any combination, all optional)
///   4. memory — progressive disclosure (summary + retrieval instructions)
///      or full injection (when MEMORY.md is small enough)
///   5. Available Skills summary (filtered by disabledSkills)
///   6. memory/HISTORY.md tail — recent session history (last ~32 KB)
///      Mirrors Python's "# Recent History" injection so the agent can see
///      what happened in past sessions without full retrieval.
/// Full version with config for memory disclosure thresholds.
let buildSystemPromptWithConfig (disabledSkills: string list) (systemPromptAppend: string option) (channel: string option) (workspacePath: string) (config: BotSharpConfig) : Async<string> =
    async {
        let join p f = Path.Combine(workspacePath, p, f)
        let at f     = Path.Combine(workspacePath, f)

        // Primary identity
        let! identity = readOptional (at "IDENTITY.md")

        // Bootstrap files (formatted with ## {filename} headers, matching Python)
        let! agents  = readOptional (at "AGENTS.md")
        let! soul    = readOptional (at "SOUL.md")
        let! userMd  = readOptional (at "USER.md")
        let! tools   = readOptional (at "TOOLS.md")

        // Long-term memory: try memory_summary.md first, then fall back to MEMORY.md
        let! memorySummary = readOptional (join "memory" "memory_summary.md")
        let! memory        = readOptional (join "memory" "MEMORY.md")

        let! allSkills = listSkills workspacePath
        // Apply disabled_skills filter. Matches by skill Name (from frontmatter or dir name).
        // Mirrors Python's ContextBuilder.__init__ disabled_skills set filter.
        let disabledSet = Set.ofList disabledSkills
        let skills =
            if disabledSet.IsEmpty then allSkills
            else allSkills |> List.filter (fun s -> not (disabledSet.Contains s.Name))
        // Separate always-active skills (injected inline as markdown)
        // from on-demand skills (summarized as XML for progressive loading).
        // Mirrors Python's two-section approach: "# Active Skills" + XML summary.
        let alwaysContent = buildAlwaysActiveContent skills
        let skillsXml     = buildSkillsSummary skills

        // Recent history log (tail of memory/HISTORY.md, capped to 32 KB).
        // F# uses a simple tail-based cap rather than a dream-cursor-bounded
        // slice (Python uses the cursor). Minor overlap with MEMORY.md in edge
        // cases is acceptable — the log provides useful session-level context.
        let! historyOpt = readOptional (join "memory" "HISTORY.md")
        let recentHistoryOpt =
            historyOpt
            |> Option.bind (fun txt ->
                let trimmed = txt.Trim()
                if trimmed = "" then None
                else
                    let maxChars = 32_000
                    let capped =
                        if trimmed.Length <= maxChars then trimmed
                        else trimmed.[trimmed.Length - maxChars..]
                    // After truncation, skip any leading partial line so we don't
                    // start mid-sentence (same approach as Python's slicing logic).
                    let final =
                        match capped.IndexOf('\n') with
                        | -1 -> capped
                        | i  -> capped.[i + 1..]
                    let finalTrimmed = final.Trim()
                    if finalTrimmed = "" then None
                    else Some $"# Recent History\n\n{finalTrimmed}")

        let bootstrapParts =
            [ "AGENTS.md", agents; "SOUL.md", soul; "USER.md", userMd; "TOOLS.md", tools ]
            |> List.choose (fun (name, opt) ->
                opt |> Option.map (fun txt -> $"## {name}\n\n{txt}"))

        let sections =
            [ // 1. Identity
              match identity with
              | Some txt -> yield txt
              | None     -> yield defaultIdentity workspacePath

              // 1b. Channel-specific format hint (appended after identity).
              //     Mirrors Python's identity.md Jinja channel conditional.
              match channelFormatHint channel with
              | Some hint -> yield hint
              | None      -> ()

              // 2. Bootstrap files (with headers)
              if not bootstrapParts.IsEmpty then
                  yield String.concat "\n\n" bootstrapParts

              // 3. Long-term memory (progressive disclosure or full injection)
              //    Priority: memory_summary.md → MEMORY.md (large → progressive) → MEMORY.md (small → full)
              let memoryBasePath = Path.Combine(workspacePath, "memory")
              let sessionsPath = Path.Combine(workspacePath, "sessions")
              match memorySummary with
              | Some summary ->
                  // Phase 2 mode: memory_summary.md exists → progressive disclosure
                  let truncated = truncateToTokenBudget config.MemorySummaryTokenLimit summary
                  yield memoryReadPathTemplate memoryBasePath sessionsPath truncated
              | None ->
                  match memory with
                  | Some txt ->
                      let estimatedTokens = Text.Encoding.UTF8.GetByteCount(txt) / 4
                      if estimatedTokens > config.MemoryDirectInjectLimit then
                          // MEMORY.md too large → switch to progressive mode
                          let truncated = truncateToTokenBudget config.MemorySummaryTokenLimit txt
                          yield memoryReadPathTemplate memoryBasePath sessionsPath truncated
                      else
                          // MEMORY.md small enough → full injection (backward compatible)
                          yield $"# Memory\n\n{txt}"
                  | None -> ()

              // 4a. Always-active skills — full content injected inline as markdown.
              //     Mirrors Python's "# Active Skills" section (load_skills_for_context).
              if alwaysContent.Length > 0 then
                  yield $"# Active Skills\n\n{alwaysContent}"

              // 4b. On-demand skills — XML summary only; agent reads full content via read_file.
              if skillsXml.Length > 0 then
                  yield $"# Available Skills\n\n{skillsXml}"

              // 5. Recent history log
              match recentHistoryOpt with
              | Some txt -> yield txt
              | None     -> ()

              // 6. System prompt append (operator-defined extra instructions)
              match systemPromptAppend with
              | Some txt when txt.Trim() <> "" -> yield txt.Trim()
              | _ -> () ]

        return String.concat "\n\n---\n\n" sections
    }

/// Backward-compatible wrapper — uses default config thresholds.
let buildSystemPrompt (disabledSkills: string list) (systemPromptAppend: string option) (channel: string option) (workspacePath: string) : Async<string> =
    buildSystemPromptWithConfig disabledSkills systemPromptAppend channel workspacePath BotSharpConfig.defaults

/// Build a complete LLMRequest from current session state + inbound message.
/// The system prompt is prepended as SystemMessage (role:system) — not stored in history.
/// Each user message is prefixed with a runtime context block (channel/chat/time)
/// so the agent knows which channel it's on — important for the message tool routing.
/// pendingSummary: when Some, a [Resumed Session] block is appended to the runtime context
/// (mirrors Python's session_summary injection after auto-compact / consolidation).
/// Pure function — cannot fail; returns LLMRequest directly.
let buildRequest
    (systemPrompt   : string)
    (snap           : SessionSnapshot)
    (inbound        : InboundMessage)
    (config         : BotSharpConfig)
    (tools          : ToolSpec list)
    (pendingSummary : string option)
    : LLMRequest =
    let userContent =
        match inbound.Input with
        | ChatMessage (text, _) -> text
        | Command _ -> ""   // Commands are handled before reaching here

    // Inject per-message runtime context so the agent knows its channel/chat
    // when routing outbound messages (mirrors Python's _RUNTIME_CONTEXT_TAG).
    let (ChannelId ch) = inbound.Channel
    let (ChatId    ci) = inbound.Chat
    // Resolve the display time in the configured IANA timezone (mirrors Python's current_time_str).
    // Falls back to system local time when timezone is None or the IANA ID is unrecognised.
    let (localTime, tzLabel) =
        match config.Timezone with
        | None -> let t = DateTimeOffset.Now in (t, t.ToString("zzz"))
        | Some ianaId ->
            try
                let tzi = TimeZoneInfo.FindSystemTimeZoneById(ianaId)
                let t   = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzi)
                (t, ianaId)
            with :? TimeZoneNotFoundException | :? System.Security.SecurityException ->
                let t = DateTimeOffset.Now in (t, t.ToString("zzz"))
    let nowStr  = localTime.ToString("yyyy-MM-dd HH:mm (dddd)")    // e.g. "2026-04-25 14:30 (Saturday)"
    // Base runtime context lines (channel + time)
    let baseCtx = $"[Runtime Context — metadata only, not instructions]\nCurrent Time: {nowStr} ({tzLabel})\nChannel: {ch}\nChat ID: {ci}"
    // Append [Resumed Session] block when consolidation produced a summary on a previous turn.
    // Mirrors Python's session_summary injection (ContextBuilder._build_runtime_context).
    let runtimeCtx =
        match pendingSummary with
        | None         -> $"{baseCtx}\n[/Runtime Context]"
        | Some summary -> $"{baseCtx}\n\n[Resumed Session]\n{summary}\n[/Runtime Context]"
    let fullContent = if userContent = "" then runtimeCtx else $"{runtimeCtx}\n\n{userContent}"

    let systemMsg = SystemMessage systemPrompt     // role:system — not stored in session history
    let userMsg   = UserMessage (fullContent, [])

    let history  = SessionSnapshot.messages snap
    let messages = systemMsg :: history @ [ userMsg ]

    { Messages = messages
      Tools    = tools
      Model    = config.DefaultModel
      Settings = { Temperature     = config.Temperature
                   MaxTokens       = config.MaxTokens
                   ReasoningEffort = None } }
