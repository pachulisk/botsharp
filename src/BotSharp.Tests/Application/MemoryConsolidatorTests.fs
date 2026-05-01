module BotSharp.Tests.Application.MemoryConsolidatorTests

open System
open System.IO
open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.Errors
open BotSharp.Application.AgentLoop
open BotSharp.Application.MemoryConsolidator

// ═══════════════════════════════════════════════════════════════════════════
// Stub helpers (mirrors AgentLoopTests pattern)
// ═══════════════════════════════════════════════════════════════════════════

let private stubProvider (response: LLMResponse) : LLMProvider = {
    Id           = "stub"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    RetryPolicy  = RetryPolicy.standard
    Chat         = fun _ _ _ -> async { return Result.Ok response }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

let private textResponse (text: string) : LLMResponse = {
    Body             = TextOnly text
    ReasoningContent = None
    ThinkingBlocks   = []
    Usage            = { PromptTokens = 5; CompletionTokens = 10; CachedTokens = 0 }
    FinishReason     = None
}

let private saveMemoryToolCall (historyEntry: string) (memoryUpdate: string) : LLMResponse =
    let args =
        use doc = JsonDocument.Parse($"""{{
            "history_entry": {JsonSerializer.Serialize(historyEntry)},
            "memory_update": {JsonSerializer.Serialize(memoryUpdate)}
        }}""")
        doc.RootElement.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.Clone())
        |> Map.ofSeq
    let call = {
        Id           = ToolCallId "call_consolidate"
        Tool         = ToolName "save_memory"
        Arguments    = args
        ProviderMeta = None
    }
    { Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [call])
      ReasoningContent = None
      ThinkingBlocks   = []
      Usage            = { PromptTokens = 10; CompletionTokens = 20; CachedTokens = 0 }
      FinishReason     = None }

/// Build a SessionSnapshot with enough messages to trigger consolidation.
let private snapWithMessages (count: int) =
    let sid  = SessionId "test:consolidation"
    let now  = DateTimeOffset.UtcNow
    let empty = SessionSnapshot.empty sid now
    List.init count (fun i ->
        if i % 2 = 0 then UserMessage ($"msg {i}", [])
        else AssistantMessage ($"reply {i}", None))
    |> List.fold (fun s m -> SessionSnapshot.append m s) empty

/// Deps with temp workspace for file I/O assertions.
let private makeDeps (provider: LLMProvider) (workspacePath: string) : AgentDependencies = {
    Provider          = provider
    Tools             = Map.empty
    LoadSession       = fun sid -> async { return Result.Ok (SessionSnapshot.empty sid DateTimeOffset.UtcNow) }
    PersistSession    = fun _ -> async { return Result.Ok () }
    BuildSystemPrompt = fun _ _ -> async { return "stub system prompt" }
    Config            = { BotSharpConfig.defaults with
                              WorkspacePath   = workspacePath
                              MemoryWindowSize = 5 }
    StreamHook        = NoStreaming
    CronService       = None
    Hook              = AgentHook.none
    LastTokenUsage    = ref None
    CurrentIteration  = ref 0
    RuleEngine        = None
    FallbackProviders = []
    OpenStateDb       = None
    TokenTracker      = ref None }

/// Helper: run test in a temp directory; clean up on exit.
let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"consolidator-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// needsConsolidation — pure function, no I/O
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``needsConsolidation returns false for empty snapshot`` () =
    let snap = snapWithMessages 0
    let config = { BotSharpConfig.defaults with MemoryWindowSize = 5 }
    Assert.False(needsConsolidation snap config)

[<Fact>]
let ``needsConsolidation returns false when unconsolidated count is below window`` () =
    let snap = snapWithMessages 4  // window = 5
    let config = { BotSharpConfig.defaults with MemoryWindowSize = 5 }
    Assert.False(needsConsolidation snap config)

[<Fact>]
let ``needsConsolidation returns true when unconsolidated count equals window`` () =
    let snap = snapWithMessages 5
    let config = { BotSharpConfig.defaults with MemoryWindowSize = 5 }
    Assert.True(needsConsolidation snap config)

[<Fact>]
let ``needsConsolidation returns true when unconsolidated count exceeds window`` () =
    let snap = snapWithMessages 10
    let config = { BotSharpConfig.defaults with MemoryWindowSize = 5 }
    Assert.True(needsConsolidation snap config)

[<Fact>]
let ``needsConsolidation respects MemoryWindowSize configuration`` () =
    let snap = snapWithMessages 3
    let config1 = { BotSharpConfig.defaults with MemoryWindowSize = 5 }
    let config2 = { BotSharpConfig.defaults with MemoryWindowSize = 3 }
    Assert.False(needsConsolidation snap config1)
    Assert.True(needsConsolidation snap config2)

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — skipping when below threshold
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate returns ConsolidationSkipped when messages below window`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 3
        let deps = makeDeps (stubProvider (textResponse "unused")) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok ConsolidationSkipped -> ()
        | other -> Assert.Fail($"Expected ConsolidationSkipped, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — successful path with save_memory tool call
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate writes HISTORY.md when LLM returns save_memory tool call`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let response = saveMemoryToolCall "Session summary: tested consolidation." "Updated long-term memory."
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated _) ->
            let historyPath = Path.Combine(dir, "memory", "HISTORY.md")
            Assert.True(File.Exists(historyPath), "HISTORY.md should exist after consolidation")
            let content = File.ReadAllText(historyPath)
            Assert.Contains("Session summary: tested consolidation.", content)
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

[<Fact>]
let ``consolidate writes MEMORY.md when LLM returns save_memory tool call`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let response = saveMemoryToolCall "History entry." "New long-term memory content."
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated _) ->
            let memoryPath = Path.Combine(dir, "memory", "MEMORY.md")
            Assert.True(File.Exists(memoryPath), "MEMORY.md should exist after consolidation")
            let content = File.ReadAllText(memoryPath)
            Assert.Contains("New long-term memory content.", content)
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

[<Fact>]
let ``consolidate writes dream_cursor file with correct message index`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let response = saveMemoryToolCall "Summary." "Memory."
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (_, _, newIndex)) ->
            let cursorPath = Path.Combine(dir, "memory", ".dream_cursor")
            Assert.True(File.Exists(cursorPath), ".dream_cursor should exist after consolidation")
            let written = File.ReadAllText(cursorPath).Trim()
            Assert.Equal(string newIndex, written)
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

[<Fact>]
let ``consolidate returns correct newIndex equal to total message count`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let response = saveMemoryToolCall "Summary." "Memory."
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (_, _, newIndex)) ->
            Assert.Equal(6, newIndex)
        | other -> Assert.Fail($"Expected Consolidated with newIndex=6, got {other}"))

[<Fact>]
let ``consolidate cursor file index matches Consolidated result newIndex`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 8
        let response = saveMemoryToolCall "Summary." "Memory."
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (_, _, newIndex)) ->
            let cursorPath = Path.Combine(dir, "memory", ".dream_cursor")
            let written = int (File.ReadAllText(cursorPath).Trim())
            Assert.Equal(newIndex, written)
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — fallback: LLM returns plain text with markers
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate falls back to marker parsing when LLM returns plain text`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let markerText = "==HISTORY==\nThis is the history.\n==MEMORY==\nThis is the memory."
        let response = textResponse markerText
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (histEntry, _, _)) ->
            Assert.Contains("history", histEntry.ToLowerInvariant())
        | other -> Assert.Fail($"Expected Consolidated from fallback, got {other}"))

[<Fact>]
let ``consolidate still writes dream_cursor on fallback text response`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let markerText = "==HISTORY==\nFallback history.\n==MEMORY==\nFallback memory."
        let response = textResponse markerText
        let deps = makeDeps (stubProvider response) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated _) ->
            let cursorPath = Path.Combine(dir, "memory", ".dream_cursor")
            Assert.True(File.Exists(cursorPath), ".dream_cursor must be written even on text fallback")
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — memory directory is auto-created
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate creates memory subdirectory if absent`` () =
    withTempDir (fun dir ->
        // Workspace exists but memory/ does not
        let snap = snapWithMessages 6
        let response = saveMemoryToolCall "Summary." "Memory."
        let deps = makeDeps (stubProvider response) dir
        let _ = consolidate snap deps |> Async.RunSynchronously
        let memDir = Path.Combine(dir, "memory")
        Assert.True(Directory.Exists(memDir), "memory/ directory should be created automatically"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — LLM error propagation
// ═══════════════════════════════════════════════════════════════════════════

let private errorProvider : LLMProvider = {
    Id           = "err"
    DefaultModel = "stub-model"
    Capabilities = Set.empty
    // Zero retries so the test doesn't hang waiting for backoff delays.
    RetryPolicy  = { RetryPolicy.standard with Mode = FixedRetries (0, []) }
    Chat         = fun _ _ _ -> async {
        return Result.Error {
            Kind        = ServerError 503
            RawMessage  = "provider unavailable"
            ProviderCode = None
        }
    }
    ChatStream   = fun _ _ _ _ -> async { return Result.Ok () }
}

[<Fact>]
let ``consolidate returns AgentLlmFailure when LLM provider returns error`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6   // enough to trigger consolidation
        let deps = makeDeps errorProvider dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Error (AgentLlmFailure { Kind = ServerError 503 }) -> ()
        | other -> Assert.Fail($"Expected AgentLlmFailure(ServerError 503), got {other}"))

[<Fact>]
let ``consolidate LLM error does not create memory files`` () =
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let deps = makeDeps errorProvider dir
        let _ = consolidate snap deps |> Async.RunSynchronously
        // Neither HISTORY.md nor MEMORY.md should exist — write never happened
        Assert.False(File.Exists(Path.Combine(dir, "memory", "HISTORY.md")),
                     "HISTORY.md should not exist when LLM fails")
        Assert.False(File.Exists(Path.Combine(dir, "memory", "MEMORY.md")),
                     "MEMORY.md should not exist when LLM fails"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — fallback text with no markers
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate fallback with no markers treats whole response as history entry`` () =
    // When the LLM returns plain text with no ==HISTORY== / ==MEMORY== markers,
    // parseConsolidationResponse treats the whole response as the history entry
    // and keeps the existing memory unchanged.
    withTempDir (fun dir ->
        // Pre-populate MEMORY.md so we can verify it isn't overwritten.
        let memDir = Path.Combine(dir, "memory")
        Directory.CreateDirectory(memDir) |> ignore
        File.WriteAllText(Path.Combine(memDir, "MEMORY.md"), "existing memory content")
        let snap     = snapWithMessages 6
        let response = textResponse "Just a plain summary — no markers here."
        let deps     = makeDeps (stubProvider response) dir
        let result   = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (histEntry, _, _)) ->
            // History entry should be the whole response (trimmed)
            Assert.Contains("plain summary", histEntry)
            // MEMORY.md should be unchanged (no ==MEMORY== to update from)
            let memContent = File.ReadAllText(Path.Combine(memDir, "MEMORY.md"))
            Assert.Contains("existing memory content", memContent)
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — Empty response body
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate with Empty response body still returns Consolidated and writes cursor`` () =
    // LLM returns Empty (no content at all).
    // historyEntry="" and memoryUpdate="" → no HISTORY.md/MEMORY.md written,
    // but .dream_cursor should still be persisted.
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let emptyResp : LLMResponse = {
            Body             = Empty
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 1; CompletionTokens = 0; CachedTokens = 0 }
            FinishReason     = None
        }
        let deps = makeDeps (stubProvider emptyResp) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (histEntry, _, newIndex)) ->
            Assert.Equal("", histEntry)
            Assert.Equal(6, newIndex)
            let cursorPath = Path.Combine(dir, "memory", ".dream_cursor")
            Assert.True(File.Exists(cursorPath), ".dream_cursor must be written even for Empty body")
        | other -> Assert.Fail($"Expected Consolidated for Empty body, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — save_memory with history_entry only (memory_update absent)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate with save_memory missing memory_update uses empty string for memory`` () =
    // Exercises extractFromToolCall's | Some h, None -> Some (h, "") branch.
    // When memory_update is absent the consolidation still writes HISTORY.md
    // and treats memoryUpdate as "" (no MEMORY.md overwrite since "" = currentMem = "").
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        // Build a save_memory call that has only history_entry, no memory_update
        let args =
            let doc = JsonDocument.Parse("""{"history_entry": "History only — no memory_update."}""")
            doc.RootElement.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.Clone())
            |> Map.ofSeq
        let call = { Id = ToolCallId "c1"; Tool = ToolName "save_memory"; Arguments = args; ProviderMeta = None }
        let resp : LLMResponse = {
            Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [call])
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 5; CompletionTokens = 5; CachedTokens = 0 }
            FinishReason     = None
        }
        let deps = makeDeps (stubProvider resp) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (histEntry, _, newIndex)) ->
            Assert.Contains("History only", histEntry)
            Assert.Equal(6, newIndex)
            // HISTORY.md should exist; MEMORY.md should NOT (empty string skipped)
            Assert.True(File.Exists(Path.Combine(dir, "memory", "HISTORY.md")),
                        "HISTORY.md should be written for non-empty historyEntry")
        | other -> Assert.Fail($"Expected Consolidated, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — memory unchanged when memoryUpdate equals currentMem
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate does not rewrite MEMORY.md when memoryUpdate equals current memory`` () =
    // Line 215: `if memoryUpdate <> currentMem && memoryUpdate <> "" then writeMemory`
    // When the LLM echoes the current memory unchanged, the file is not overwritten.
    withTempDir (fun dir ->
        let memDir  = Path.Combine(dir, "memory")
        Directory.CreateDirectory(memDir) |> ignore
        let existingMemory = "unchanged long-term memory"
        File.WriteAllText(Path.Combine(memDir, "MEMORY.md"), existingMemory)
        let snap = snapWithMessages 6
        // LLM returns the same text as the current memory
        let response = saveMemoryToolCall "New history." existingMemory
        let deps     = makeDeps (stubProvider response) dir
        let _        = consolidate snap deps |> Async.RunSynchronously
        // Content should still match — no write needed
        let content = File.ReadAllText(Path.Combine(memDir, "MEMORY.md"))
        Assert.Equal(existingMemory, content))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — save_memory call present but history_entry absent (| _ -> None branch)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate with save_memory call lacking history_entry falls back to empty result`` () =
    // extractFromToolCall | _ -> None: save_memory exists but has no valid history_entry.
    // Code path: callOpt = None → parseConsolidationResponse "" currentMem → ("", currentMem)
    // → historyEntry = "" → HISTORY.md not written; cursor IS written.
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        // save_memory call with ONLY memory_update (no history_entry key)
        let args =
            let doc = JsonDocument.Parse("""{"memory_update": "Some memory but no history."}""")
            doc.RootElement.EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.Clone())
            |> Map.ofSeq
        let call = { Id = ToolCallId "c1"; Tool = ToolName "save_memory"; Arguments = args; ProviderMeta = None }
        let resp : LLMResponse = {
            Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [call])
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 5; CompletionTokens = 5; CachedTokens = 0 }
            FinishReason     = None
        }
        let deps = makeDeps (stubProvider resp) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (histEntry, _, newIndex)) ->
            Assert.Equal("", histEntry)
            Assert.Equal(6, newIndex)
            // HISTORY.md should NOT exist (historyEntry is "")
            Assert.False(File.Exists(Path.Combine(dir, "memory", "HISTORY.md")),
                         "HISTORY.md should not exist when history_entry is absent")
            // Cursor should exist
            Assert.True(File.Exists(Path.Combine(dir, "memory", ".dream_cursor")),
                        ".dream_cursor must be written even when history_entry is absent")
        | other -> Assert.Fail($"Expected Consolidated for no-history_entry save_memory, got {other}"))

// ═══════════════════════════════════════════════════════════════════════════
// consolidate — WithToolCalls but no save_memory call (fallback to empty history)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``consolidate falls back to empty history when WithToolCalls contains no save_memory call`` () =
    // LLM returns a tool call for a different tool — no save_memory present.
    // extractFromToolCall returns None → parseConsolidationResponse "" "" →
    // historyEntry="" so HISTORY.md is not written; cursor IS written.
    withTempDir (fun dir ->
        let snap = snapWithMessages 6
        let otherCall = {
            Id           = ToolCallId "c1"
            Tool         = ToolName "some_other_tool"
            Arguments    = Map.empty
            ProviderMeta = None
        }
        let noSaveMemResp : LLMResponse = {
            Body             = WithToolCalls (None, NonEmptyList.ofListUnsafe [otherCall])
            ReasoningContent = None
            ThinkingBlocks   = []
            Usage            = { PromptTokens = 5; CompletionTokens = 3; CachedTokens = 0 }
            FinishReason     = None
        }
        let deps = makeDeps (stubProvider noSaveMemResp) dir
        let result = consolidate snap deps |> Async.RunSynchronously
        match result with
        | Result.Ok (Consolidated (histEntry, _, newIndex)) ->
            Assert.Equal("", histEntry)
            Assert.Equal(6, newIndex)
            // No history was written (histEntry = "")
            Assert.False(File.Exists(Path.Combine(dir, "memory", "HISTORY.md")),
                         "HISTORY.md should not exist when historyEntry is empty")
            // Cursor should still exist
            Assert.True(File.Exists(Path.Combine(dir, "memory", ".dream_cursor")),
                        ".dream_cursor should be written")
        | other -> Assert.Fail($"Expected Consolidated for no-save_memory tool call, got {other}"))
