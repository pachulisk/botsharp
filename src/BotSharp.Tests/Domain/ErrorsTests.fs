module BotSharp.Tests.Domain.ErrorsTests

open System
open Xunit
open BotSharp.Domain.Types
open BotSharp.Domain.Errors

// ── helpers ─────────────────────────────────────────────────────────────────

let private makeLlmError kind =
    { Kind = kind; RawMessage = "raw"; ProviderCode = None }

let private assertNonEmpty (s: string) =
    Assert.False(String.IsNullOrEmpty(s), "formatError should return a non-empty string")

// ── AgentParseFailure ────────────────────────────────────────────────────────

[<Fact>]
let ``formatError JsonParseError contains the message`` () =
    let result = formatError (AgentParseFailure (JsonParseError ("unexpected token", 42)))
    assertNonEmpty result
    Assert.Contains("unexpected token", result)

[<Fact>]
let ``formatError SchemaError contains the field name`` () =
    let result = formatError (AgentParseFailure (SchemaError ("temperature", "must be a float")))
    assertNonEmpty result
    Assert.Contains("temperature", result)

[<Fact>]
let ``formatError UnknownField contains the field name`` () =
    let result = formatError (AgentParseFailure (UnknownField "bogus_key"))
    assertNonEmpty result
    Assert.Contains("bogus_key", result)

[<Fact>]
let ``formatError MissingField contains the field name`` () =
    let result = formatError (AgentParseFailure (MissingField "api_key"))
    assertNonEmpty result
    Assert.Contains("api_key", result)

// ── AgentLlmFailure ──────────────────────────────────────────────────────────

[<Fact>]
let ``formatError RateLimited is non-empty`` () =
    let result = formatError (AgentLlmFailure (makeLlmError (RateLimited None)))
    assertNonEmpty result

[<Fact>]
let ``formatError RateLimited with retryAfter is non-empty`` () =
    let result = formatError (AgentLlmFailure (makeLlmError (RateLimited (Some (TimeSpan.FromSeconds 5.)))))
    assertNonEmpty result

[<Fact>]
let ``formatError QuotaExceeded is non-empty`` () =
    let result = formatError (AgentLlmFailure (makeLlmError QuotaExceeded))
    assertNonEmpty result

[<Fact>]
let ``formatError ContextTooLong is non-empty`` () =
    let result = formatError (AgentLlmFailure (makeLlmError ContextTooLong))
    assertNonEmpty result

[<Fact>]
let ``formatError ModelNotFound contains the model name`` () =
    let result = formatError (AgentLlmFailure (makeLlmError (ModelNotFound "gpt-99")))
    assertNonEmpty result
    Assert.Contains("gpt-99", result)

[<Fact>]
let ``formatError Timeout is non-empty`` () =
    let result = formatError (AgentLlmFailure (makeLlmError (Timeout RequestTimeout)))
    assertNonEmpty result

[<Fact>]
let ``formatError ServerError contains the HTTP status code`` () =
    let result = formatError (AgentLlmFailure (makeLlmError (ServerError 503)))
    assertNonEmpty result
    Assert.Contains("503", result)

[<Fact>]
let ``formatError ConnectionFailed is non-empty`` () =
    let result = formatError (AgentLlmFailure (makeLlmError (ConnectionFailed "ECONNREFUSED")))
    assertNonEmpty result

[<Fact>]
let ``formatError MalformedResponse is non-empty`` () =
    let inner = JsonParseError ("bad json", 0)
    let result = formatError (AgentLlmFailure (makeLlmError (MalformedResponse inner)))
    assertNonEmpty result

// ── AgentToolFailure ─────────────────────────────────────────────────────────

[<Fact>]
let ``formatError WorkspaceViolation contains the path`` () =
    let result = formatError (AgentToolFailure (WorkspaceViolation "/etc/passwd"))
    assertNonEmpty result
    Assert.Contains("/etc/passwd", result)

[<Fact>]
let ``formatError ToolNotFound contains the tool name`` () =
    let result = formatError (AgentToolFailure (ToolNotFound (ToolName "read_file")))
    assertNonEmpty result
    Assert.Contains("read_file", result)

[<Fact>]
let ``formatError ExecutionTimeout is non-empty`` () =
    let result = formatError (AgentToolFailure (ExecutionTimeout (TimeSpan.FromSeconds 30.)))
    assertNonEmpty result

[<Fact>]
let ``formatError ExecutionFailed contains the message`` () =
    let result = formatError (AgentToolFailure (ExecutionFailed "process exited with code 1"))
    assertNonEmpty result
    Assert.Contains("process exited with code 1", result)

[<Fact>]
let ``formatError ToolError fallthrough ParameterMissing is non-empty`` () =
    // ParameterMissing matches the wildcard arm for AgentToolFailure
    let result = formatError (AgentToolFailure (ParameterMissing "count"))
    assertNonEmpty result

// ── AgentChannelFailure ──────────────────────────────────────────────────────

[<Fact>]
let ``formatError NotAuthenticated is non-empty`` () =
    let result = formatError (AgentChannelFailure NotAuthenticated)
    assertNonEmpty result

[<Fact>]
let ``formatError MessageTooLong is non-empty`` () =
    let result = formatError (AgentChannelFailure (MessageTooLong (5000, 4096)))
    assertNonEmpty result

[<Fact>]
let ``formatError ChannelError fallthrough ChannelRateLimited is non-empty`` () =
    let result = formatError (AgentChannelFailure (ChannelRateLimited (TimeSpan.FromSeconds 10.)))
    assertNonEmpty result

// ── AgentStorageFailure ──────────────────────────────────────────────────────

[<Fact>]
let ``formatError FileNotFound contains the path`` () =
    let result = formatError (AgentStorageFailure (FileNotFound "/sessions/abc.json"))
    assertNonEmpty result
    Assert.Contains("/sessions/abc.json", result)

[<Fact>]
let ``formatError StorageError fallthrough WriteFailure is non-empty`` () =
    let result = formatError (AgentStorageFailure (WriteFailure "disk full"))
    assertNonEmpty result

// ── MaxIterationsReached / SessionActorStopped ───────────────────────────────

[<Fact>]
let ``formatError MaxIterationsReached contains the count`` () =
    let result = formatError (MaxIterationsReached 15)
    assertNonEmpty result
    Assert.Contains("15", result)

[<Fact>]
let ``formatError SessionActorStopped is non-empty`` () =
    let result = formatError SessionActorStopped
    assertNonEmpty result
