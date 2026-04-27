module BotSharp.Tests.Application.ContextBuilderTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.ContextBuilder

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private now = DateTimeOffset.UtcNow

let private emptySnap =
    SessionSnapshot.empty (SessionId "test:chat") now

let private snapWithMessages (msgs: Message list) : SessionSnapshot =
    msgs |> List.fold (fun s m -> SessionSnapshot.append m s) emptySnap

let private makeInbound (text: string) : InboundMessage =
    { Channel            = ChannelId "cli"
      Sender             = UserId "user"
      Chat               = ChatId "chat"
      Input              = ChatMessage (text, [])
      Metadata           = Map.empty
      SessionKeyOverride = None }

let private defaultConfig = BotSharpConfig.defaults

// ═══════════════════════════════════════════════════════════════════════════
// Message count tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequest with empty snapshot produces exactly 2 messages`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    Assert.Equal(2, List.length req.Messages)

[<Fact>]
let ``buildRequest with 2-message snapshot produces 4 messages total`` () =
    let snap =
        snapWithMessages [ UserMessage ("q", []); AssistantMessage ("a", None) ]
    let req = buildRequest "sys" snap (makeInbound "hi") defaultConfig [] None
    Assert.Equal(4, List.length req.Messages)

// ═══════════════════════════════════════════════════════════════════════════
// Message ordering tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequest places system prompt as first message`` () =
    let req = buildRequest "SYSTEM" emptySnap (makeInbound "hello") defaultConfig [] None
    match List.head req.Messages with
    | SystemMessage "SYSTEM" -> ()
    | other -> Assert.Fail($"Expected SystemMessage first, got {other}")

[<Fact>]
let ``buildRequest places user chat text as last message`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "user text") defaultConfig [] None
    match List.last req.Messages with
    | UserMessage (content, []) when content.Contains("user text") -> ()
    | other -> Assert.Fail($"Expected user UserMessage containing 'user text' last, got {other}")

[<Fact>]
let ``buildRequest places history messages between system and user`` () =
    let histMsg1 = UserMessage ("past q", [])
    let histMsg2 = AssistantMessage ("past a", None)
    let snap = snapWithMessages [ histMsg1; histMsg2 ]
    let req = buildRequest "sys" snap (makeInbound "new") defaultConfig [] None
    // Expect: [system; histMsg1; histMsg2; user]
    let msgs = req.Messages
    Assert.Equal(4, List.length msgs)
    Assert.Equal(histMsg1, msgs[1])
    Assert.Equal(histMsg2, msgs[2])

[<Fact>]
let ``buildRequest system prompt is not the last message`` () =
    let req = buildRequest "SYSTEM" emptySnap (makeInbound "hi") defaultConfig [] None
    let last = List.last req.Messages
    match last with
    | SystemMessage "SYSTEM" -> Assert.Fail("System prompt must not be last")
    | _ -> ()

[<Fact>]
let ``buildRequest user text is not the first message`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "USER_TEXT") defaultConfig [] None
    let first = List.head req.Messages
    match first with
    | UserMessage ("USER_TEXT", []) -> Assert.Fail("User text must not be first")
    | _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// Config propagation tests
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequest model equals config DefaultModel`` () =
    let config = { defaultConfig with DefaultModel = "gpt-4-turbo" }
    let req = buildRequest "sys" emptySnap (makeInbound "hi") config [] None
    Assert.Equal("gpt-4-turbo", req.Model)

[<Fact>]
let ``buildRequest settings Temperature equals config Temperature`` () =
    let config = { defaultConfig with Temperature = 0.42 }
    let req = buildRequest "sys" emptySnap (makeInbound "hi") config [] None
    Assert.Equal(0.42, req.Settings.Temperature)

[<Fact>]
let ``buildRequest settings MaxTokens equals config MaxTokens`` () =
    let config = { defaultConfig with MaxTokens = 1234 }
    let req = buildRequest "sys" emptySnap (makeInbound "hi") config [] None
    Assert.Equal(1234, req.Settings.MaxTokens)

[<Fact>]
let ``buildRequest uses BotSharpConfig defaults correctly`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    Assert.Equal(defaultConfig.DefaultModel, req.Model)
    Assert.Equal(defaultConfig.Temperature, req.Settings.Temperature)
    Assert.Equal(defaultConfig.MaxTokens, req.Settings.MaxTokens)

// ═══════════════════════════════════════════════════════════════════════════
// Tools passthrough test
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequest passes tools list through to result`` () =
    let tool = {
        Name        = ToolName "my_tool"
        Description = "does something"
        Parameters  = Map.empty
        ConcurrencySafe = false
    }
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [ tool ] None
    Assert.Equal(1, List.length req.Tools)
    Assert.Equal(ToolName "my_tool", req.Tools[0].Name)

[<Fact>]
let ``buildRequest with no tools produces empty tools list`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    Assert.Empty(req.Tools)

// ═══════════════════════════════════════════════════════════════════════════
// Command input produces empty user text
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequest with Command input produces runtime context as last message`` () =
    let inbound =
        { Channel            = ChannelId "cli"
          Sender             = UserId "user"
          Chat               = ChatId "chat"
          Input              = Command NewSession
          Metadata           = Map.empty
          SessionKeyOverride = None }
    let req = buildRequest "sys" emptySnap inbound defaultConfig [] None
    match List.last req.Messages with
    | UserMessage (content, []) when content.StartsWith("[Runtime Context") -> ()
    | other -> Assert.Fail($"Expected runtime-context UserMessage for Command input, got {other}")

[<Fact>]
let ``buildRequest with pendingSummary injects Resumed Session block into runtime context`` () =
    // When pendingSummary = Some text, [Resumed Session] and the summary should appear
    // inside the runtime context block of the last (user) message.
    let summary = "Previous session: worked on feature X."
    let req = buildRequest "sys" emptySnap (makeInbound "hello") defaultConfig [] (Some summary)
    match List.last req.Messages with
    | UserMessage (content, _) ->
        Assert.Contains("[Resumed Session]", content)
        Assert.Contains(summary, content)
        Assert.Contains("[Runtime Context", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``buildRequest with pendingSummary=None does not include Resumed Session block`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hello") defaultConfig [] None
    match List.last req.Messages with
    | UserMessage (content, _) ->
        Assert.DoesNotContain("[Resumed Session]", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

// ── timezone in buildRequest ──────────────────────────────────────────────────

[<Fact>]
let ``buildRequest with Timezone None emits offset notation in runtime context`` () =
    let cfg = { defaultConfig with Timezone = None }
    let req = buildRequest "sys" emptySnap (makeInbound "hi") cfg [] None
    match List.last req.Messages with
    | UserMessage (content, _) ->
        // System local time — offset like "+08:00" or "+00:00" present
        Assert.Contains("Current Time:", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``buildRequest with Timezone Some emits IANA name in runtime context`` () =
    // Use UTC which is always valid on any platform.
    let cfg = { defaultConfig with Timezone = Some "UTC" }
    let req = buildRequest "sys" emptySnap (makeInbound "hi") cfg [] None
    match List.last req.Messages with
    | UserMessage (content, _) ->
        Assert.Contains("Current Time:", content)
        Assert.Contains("UTC", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``buildRequest with unrecognised Timezone falls back to local time without crashing`` () =
    let cfg = { defaultConfig with Timezone = Some "Not/A/Real/Timezone" }
    // Should not throw; falls back to system local time silently.
    let req = buildRequest "sys" emptySnap (makeInbound "hi") cfg [] None
    Assert.Equal(2, List.length req.Messages)   // system + user — no crash

// ═══════════════════════════════════════════════════════════════════════════
// buildSystemPrompt — Recent History (memory/HISTORY.md) tests
// ═══════════════════════════════════════════════════════════════════════════

/// Helper: create a temp workspace, run buildSystemPrompt (no disabled skills, no channel), return the prompt.
let private withSystemPrompt (setup: string -> unit) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"ctx-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try
        setup dir
        let prompt = buildSystemPrompt [] None None dir |> Async.RunSynchronously
        f prompt
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// Helper: run buildSystemPrompt with a specific disabled-skills list.
let private withSystemPromptDisabled (disabled: string list) (setup: string -> unit) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"ctx-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try
        setup dir
        let prompt = buildSystemPrompt disabled None None dir |> Async.RunSynchronously
        f prompt
    finally
        try Directory.Delete(dir, true) with _ -> ()

/// Helper: run buildSystemPrompt with a specific channel.
let private withSystemPromptChannel (channel: string option) (setup: string -> unit) (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"ctx-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try
        setup dir
        let prompt = buildSystemPrompt [] None channel dir |> Async.RunSynchronously
        f prompt
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``buildSystemPrompt includes Recent History section when HISTORY.md exists`` () =
    withSystemPrompt
        (fun dir ->
            let memDir = Path.Combine(dir, "memory")
            Directory.CreateDirectory(memDir) |> ignore
            File.WriteAllText(Path.Combine(memDir, "HISTORY.md"), "## Session 1\n\nDid something useful."))
        (fun prompt ->
            Assert.Contains("Recent History", prompt)
            Assert.Contains("Did something useful", prompt))

[<Fact>]
let ``buildSystemPrompt omits Recent History section when HISTORY.md is absent`` () =
    withSystemPrompt
        (fun _ -> ())   // no HISTORY.md created
        (fun prompt ->
            Assert.DoesNotContain("Recent History", prompt))

[<Fact>]
let ``buildSystemPrompt omits Recent History section when HISTORY.md is empty`` () =
    withSystemPrompt
        (fun dir ->
            let memDir = Path.Combine(dir, "memory")
            Directory.CreateDirectory(memDir) |> ignore
            File.WriteAllText(Path.Combine(memDir, "HISTORY.md"), "   "))
        (fun prompt ->
            Assert.DoesNotContain("Recent History", prompt))

[<Fact>]
let ``buildSystemPrompt caps Recent History at 32000 chars and skips leading partial line`` () =
    withSystemPrompt
        (fun dir ->
            let memDir = Path.Combine(dir, "memory")
            Directory.CreateDirectory(memDir) |> ignore
            // Write 40 KB of history (exceeds 32 KB cap)
            let line = String.replicate 100 "x"
            let lines = Seq.init 400 (fun i -> $"line-{i}: {line}") |> String.concat "\n"
            File.WriteAllText(Path.Combine(memDir, "HISTORY.md"), lines))
        (fun prompt ->
            Assert.Contains("Recent History", prompt)
            // Should contain tail content (large line numbers), not line-0
            Assert.DoesNotContain("line-0:", prompt))

// ═══════════════════════════════════════════════════════════════════════════
// buildSystemPrompt — bootstrap files (AGENTS.md, SOUL.md, USER.md, TOOLS.md)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildSystemPrompt includes AGENTS.md with header when file exists`` () =
    withSystemPrompt
        (fun dir ->
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "Agent capabilities."))
        (fun prompt ->
            Assert.Contains("## AGENTS.md", prompt)
            Assert.Contains("Agent capabilities.", prompt))

[<Fact>]
let ``buildSystemPrompt includes SOUL.md when file exists`` () =
    withSystemPrompt
        (fun dir ->
            File.WriteAllText(Path.Combine(dir, "SOUL.md"), "Be kind."))
        (fun prompt ->
            Assert.Contains("## SOUL.md", prompt)
            Assert.Contains("Be kind.", prompt))

[<Fact>]
let ``buildSystemPrompt omits bootstrap sections when files are absent`` () =
    withSystemPrompt
        (fun _ -> ())   // no bootstrap files
        (fun prompt ->
            Assert.DoesNotContain("## AGENTS.md", prompt)
            Assert.DoesNotContain("## SOUL.md", prompt)
            Assert.DoesNotContain("## USER.md", prompt)
            Assert.DoesNotContain("## TOOLS.md", prompt))

[<Fact>]
let ``buildSystemPrompt includes IDENTITY.md when file exists`` () =
    withSystemPrompt
        (fun dir ->
            File.WriteAllText(Path.Combine(dir, "IDENTITY.md"), "You are a specialized assistant."))
        (fun prompt ->
            Assert.Contains("You are a specialized assistant.", prompt)
            // Custom identity replaces the built-in default "BotSharp" persona
            Assert.DoesNotContain("You are BotSharp", prompt))

[<Fact>]
let ``buildSystemPrompt uses default identity when IDENTITY.md is absent`` () =
    withSystemPrompt
        (fun _ -> ())
        (fun prompt ->
            Assert.Contains("BotSharp", prompt))

[<Fact>]
let ``buildSystemPrompt includes Memory section when MEMORY.md exists`` () =
    withSystemPrompt
        (fun dir ->
            let memDir = Path.Combine(dir, "memory")
            Directory.CreateDirectory(memDir) |> ignore
            File.WriteAllText(Path.Combine(memDir, "MEMORY.md"), "User prefers concise replies."))
        (fun prompt ->
            Assert.Contains("# Memory", prompt)
            Assert.Contains("User prefers concise replies.", prompt))

[<Fact>]
let ``buildSystemPrompt includes USER.md with header when file exists`` () =
    withSystemPrompt
        (fun dir ->
            File.WriteAllText(Path.Combine(dir, "USER.md"), "User context here."))
        (fun prompt ->
            Assert.Contains("## USER.md", prompt)
            Assert.Contains("User context here.", prompt))

[<Fact>]
let ``buildSystemPrompt includes TOOLS.md with header when file exists`` () =
    withSystemPrompt
        (fun dir ->
            File.WriteAllText(Path.Combine(dir, "TOOLS.md"), "Custom tool docs."))
        (fun prompt ->
            Assert.Contains("## TOOLS.md", prompt)
            Assert.Contains("Custom tool docs.", prompt))

[<Fact>]
let ``buildSystemPrompt includes all bootstrap files when all exist`` () =
    withSystemPrompt
        (fun dir ->
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "agents content")
            File.WriteAllText(Path.Combine(dir, "SOUL.md"),   "soul content")
            File.WriteAllText(Path.Combine(dir, "USER.md"),   "user content")
            File.WriteAllText(Path.Combine(dir, "TOOLS.md"),  "tools content"))
        (fun prompt ->
            Assert.Contains("## AGENTS.md", prompt)
            Assert.Contains("## SOUL.md",   prompt)
            Assert.Contains("## USER.md",   prompt)
            Assert.Contains("## TOOLS.md",  prompt))

[<Fact>]
let ``buildSystemPrompt omits Memory section when MEMORY.md is absent`` () =
    withSystemPrompt
        (fun _ -> ())
        (fun prompt ->
            Assert.DoesNotContain("# Memory", prompt))

[<Fact>]
let ``buildSystemPrompt includes single-line history when HISTORY.md has no newline`` () =
    // Covers the `| -1 -> capped` branch: content with no '\n' → entire content used as-is.
    withSystemPrompt
        (fun dir ->
            let memDir = Path.Combine(dir, "memory")
            Directory.CreateDirectory(memDir) |> ignore
            File.WriteAllText(Path.Combine(memDir, "HISTORY.md"), "one-liner history entry"))
        (fun prompt ->
            Assert.Contains("Recent History", prompt)
            Assert.Contains("one-liner history entry", prompt))

// ═══════════════════════════════════════════════════════════════════════════
// buildSystemPrompt — disabled_skills filtering
// ═══════════════════════════════════════════════════════════════════════════

let private writeSkill (skillsDir: string) (dirName: string) (frontmatter: string) (body: string) =
    let skillDir = Path.Combine(skillsDir, dirName)
    Directory.CreateDirectory(skillDir) |> ignore
    File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), $"---\n{frontmatter}\n---\n{body}")

[<Fact>]
let ``buildSystemPrompt includes all skills when disabledSkills is empty`` () =
    withSystemPromptDisabled []
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "alpha" "name: alpha\ndescription: alpha skill\nactivation: always" "alpha body"
            writeSkill sd "beta"  "name: beta\ndescription: beta skill"  "beta body")
        (fun prompt ->
            Assert.Contains("alpha", prompt)
            Assert.Contains("beta",  prompt))

[<Fact>]
let ``buildSystemPrompt excludes named skill when in disabledSkills`` () =
    withSystemPromptDisabled ["beta"]
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "alpha" "name: alpha\ndescription: alpha skill\nactivation: always" "alpha body"
            writeSkill sd "beta"  "name: beta\ndescription: beta skill"  "beta body")
        (fun prompt ->
            Assert.Contains("alpha", prompt)
            Assert.DoesNotContain("beta", prompt))

[<Fact>]
let ``buildSystemPrompt excludes multiple skills when all listed in disabledSkills`` () =
    withSystemPromptDisabled ["alpha"; "beta"]
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "alpha" "name: alpha\ndescription: alpha skill\nactivation: always" "alpha body"
            writeSkill sd "beta"  "name: beta\ndescription: beta skill"  "beta body"
            writeSkill sd "gamma" "name: gamma\ndescription: gamma skill" "gamma body")
        (fun prompt ->
            Assert.DoesNotContain("alpha body", prompt)
            Assert.DoesNotContain("beta",       prompt)
            Assert.Contains("gamma",            prompt))

[<Fact>]
let ``buildSystemPrompt disabledSkills is case-sensitive and does not exclude partial name matches`` () =
    // "Alpha" (capital A) should NOT exclude "alpha" (lowercase).
    withSystemPromptDisabled ["Alpha"]
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "alpha" "name: alpha\ndescription: alpha skill\nactivation: always" "alpha body")
        (fun prompt ->
            Assert.Contains("alpha", prompt))

[<Fact>]
let ``buildSystemPrompt disabledSkills with nonexistent name leaves other skills intact`` () =
    withSystemPromptDisabled ["no-such-skill"]
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "alpha" "name: alpha\ndescription: alpha skill\nactivation: always" "alpha body")
        (fun prompt ->
            Assert.Contains("alpha", prompt))

// ── systemPromptAppend ───────────────────────────────────────────────────────

[<Fact>]
let ``buildSystemPrompt with systemPromptAppend None does not add extra section`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"ctx-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try
        let prompt = buildSystemPrompt [] None None dir |> Async.RunSynchronously
        // A minimal prompt has no extra appended text beyond the default identity
        Assert.DoesNotContain("Always reply in French", prompt)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``buildSystemPrompt with systemPromptAppend Some appends the text`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"ctx-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try
        let prompt = buildSystemPrompt [] (Some "Always reply in French.") None dir |> Async.RunSynchronously
        Assert.Contains("Always reply in French.", prompt)
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``buildSystemPrompt with systemPromptAppend whitespace-only does not add section`` () =
    let dir = Path.Combine(Path.GetTempPath(), $"ctx-test-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try
        let prompt = buildSystemPrompt [] (Some "   ") None dir |> Async.RunSynchronously
        // Whitespace-only append is filtered out; prompt should not have stray separators
        Assert.DoesNotContain("   ", prompt)
    finally
        try Directory.Delete(dir, true) with _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// buildRequest — runtime context Channel and Chat ID
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildRequest runtime context includes Channel in user message`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    match List.last req.Messages with
    | UserMessage (content, _) ->
        Assert.Contains("Channel: cli", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``buildRequest runtime context includes Chat ID in user message`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    match List.last req.Messages with
    | UserMessage (content, _) ->
        Assert.Contains("Chat ID: chat", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``buildRequest runtime context tag is present in user message`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    match List.last req.Messages with
    | UserMessage (content, _) ->
        Assert.Contains("[Runtime Context", content)
        Assert.Contains("[/Runtime Context]", content)
    | other -> Assert.Fail($"Expected UserMessage, got {other}")

[<Fact>]
let ``buildRequest system message does not contain Channel or runtime context`` () =
    let req = buildRequest "sys" emptySnap (makeInbound "hi") defaultConfig [] None
    match List.head req.Messages with
    | SystemMessage content ->
        Assert.DoesNotContain("Channel:", content)
        Assert.DoesNotContain("Chat ID:", content)
    | other -> Assert.Fail($"Expected SystemMessage first, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// buildSystemPrompt — channel format hints
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildSystemPrompt telegram channel injects messaging-app Format Hint`` () =
    withSystemPromptChannel (Some "telegram")
        (fun _ -> ())
        (fun prompt ->
            Assert.Contains("Format Hint", prompt)
            Assert.Contains("messaging app", prompt))

[<Fact>]
let ``buildSystemPrompt whatsapp channel injects plain-text Format Hint`` () =
    withSystemPromptChannel (Some "whatsapp")
        (fun _ -> ())
        (fun prompt ->
            Assert.Contains("Format Hint", prompt)
            Assert.Contains("plain text only", prompt))

[<Fact>]
let ``buildSystemPrompt None channel produces no Format Hint`` () =
    withSystemPromptChannel None
        (fun _ -> ())
        (fun prompt ->
            Assert.DoesNotContain("Format Hint", prompt))

[<Fact>]
let ``buildSystemPrompt unknown channel produces no Format Hint`` () =
    withSystemPromptChannel (Some "feishu")
        (fun _ -> ())
        (fun prompt ->
            Assert.DoesNotContain("Format Hint", prompt))

[<Fact>]
let ``buildSystemPrompt discord channel injects messaging-app Format Hint`` () =
    withSystemPromptChannel (Some "discord")
        (fun _ -> ())
        (fun prompt ->
            Assert.Contains("Format Hint", prompt)
            Assert.Contains("messaging app", prompt))

// ═══════════════════════════════════════════════════════════════════════════
// buildSystemPrompt — always-active vs on-demand skills placement
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildSystemPrompt places always-active skill in Active Skills section with Skill header`` () =
    withSystemPrompt
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "myskill" "name: myskill\ndescription: test\nactivation: always" "always body")
        (fun prompt ->
            Assert.Contains("# Active Skills", prompt)
            Assert.Contains("### Skill: myskill", prompt))

[<Fact>]
let ``buildSystemPrompt always-active skill does not appear in Available Skills XML`` () =
    withSystemPrompt
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "myskill" "name: myskill\ndescription: test\nactivation: always" "always body")
        (fun prompt ->
            // Always-active skill must NOT appear in the XML skills index
            Assert.DoesNotContain("<name>myskill</name>", prompt))

[<Fact>]
let ``buildSystemPrompt on-demand skill appears in Available Skills XML not in Active Skills`` () =
    withSystemPrompt
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "myskill" "name: myskill\ndescription: test" "on-demand body")
        (fun prompt ->
            // On-demand skills appear in the XML summary
            Assert.Contains("<name>myskill</name>", prompt)
            // No Active Skills section when there are only on-demand skills
            Assert.DoesNotContain("# Active Skills", prompt))

[<Fact>]
let ``buildSystemPrompt always-active in Active Skills and on-demand in Available Skills XML`` () =
    withSystemPrompt
        (fun dir ->
            let sd = Path.Combine(dir, "skills")
            writeSkill sd "always-skill" "name: always-skill\ndescription: always\nactivation: always" "always content"
            writeSkill sd "ondemand-skill" "name: ondemand-skill\ndescription: on demand" "on-demand content")
        (fun prompt ->
            // Active Skills contains the always skill
            Assert.Contains("### Skill: always-skill", prompt)
            // XML index contains the on-demand skill
            Assert.Contains("<name>ondemand-skill</name>", prompt)
            // Always skill must NOT appear in the XML index
            Assert.DoesNotContain("<name>always-skill</name>", prompt))
