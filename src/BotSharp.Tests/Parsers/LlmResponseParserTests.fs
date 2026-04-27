module BotSharp.Tests.Parsers.LlmResponseParserTests

open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Providers.LlmResponseParser

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private parse (json: string) =
    use doc = JsonDocument.Parse(json)
    parseLlmResponse doc.RootElement

let private parseChunk (json: string) =
    use doc = JsonDocument.Parse(json)
    parseStreamChunk doc.RootElement

// ═══════════════════════════════════════════════════════════════════════════
// Non-streaming complete response
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``text response parses to TextOnly`` () =
    let json = """
    {
      "choices": [{ "message": { "content": "Hello!", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 10, "completion_tokens": 5, "cached_tokens": 0 }
    }"""
    match parse json with
    | Ok { Body = TextOnly "Hello!"; Usage = usage } ->
        Assert.Equal(10, usage.PromptTokens)
        Assert.Equal(5,  usage.CompletionTokens)
    | other -> Assert.Fail($"Expected TextOnly, got {other}")

[<Fact>]
let ``tool-call response parses to WithToolCalls`` () =
    let json = """
    {
      "choices": [{
        "message": {
          "role": "assistant",
          "content": null,
          "tool_calls": [{
            "id": "call_1",
            "type": "function",
            "function": { "name": "read_file", "arguments": "{\"path\":\"./README.md\"}" }
          }]
        },
        "finish_reason": "tool_calls"
      }],
      "usage": { "prompt_tokens": 20, "completion_tokens": 15, "cached_tokens": 0 }
    }"""
    match parse json with
    | Ok { Body = WithToolCalls (_, nel) } ->
        let call = nel.Head   // first (and only) tool call
        Assert.Equal(ToolCallId "call_1", call.Id)
        Assert.Equal(ToolName "read_file", call.Tool)
        Assert.True(call.Arguments.ContainsKey("path"))
    | other -> Assert.Fail($"Expected WithToolCalls, got {other}")

[<Fact>]
let ``empty choices array parses to Empty body`` () =
    let json = """{ "choices": [], "usage": { "prompt_tokens": 0, "completion_tokens": 0 } }"""
    match parse json with
    | Ok { Body = Empty } -> ()
    | other -> Assert.Fail($"Expected Empty, got {other}")

[<Fact>]
let ``null content with no tool_calls parses to Empty body`` () =
    let json = """
    { "choices": [{ "message": { "role": "assistant", "content": null },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 5, "completion_tokens": 0 } }"""
    match parse json with
    | Ok { Body = Empty } -> ()
    | other -> Assert.Fail($"Expected Empty, got {other}")

[<Fact>]
let ``missing usage field defaults to zero tokens`` () =
    let json = """
    { "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }] }"""
    match parse json with
    | Ok { Usage = usage } ->
        Assert.Equal(0, usage.PromptTokens)
        Assert.Equal(0, usage.CompletionTokens)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``reasoning_content is captured`` () =
    let json = """
    {
      "choices": [{ "message": {
                      "role": "assistant",
                      "content": "Final answer",
                      "reasoning_content": "I think..." },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 0, "completion_tokens": 0 }
    }"""
    match parse json with
    | Ok { ReasoningContent = Some r } -> Assert.Equal("I think...", r)
    | other -> Assert.Fail($"Expected reasoning_content, got {other}")

[<Fact>]
let ``missing choices field is a parse error`` () =
    let json = """{ "id": "chatcmpl-123" }"""
    match parse json with
    | Error _ -> ()
    | Ok v -> Assert.Fail($"Expected Error, got {v}")

[<Fact>]
let ``multiple tool calls in response — second call is accessible`` () =
    let json = """
    {
      "choices": [{
        "message": {
          "role": "assistant",
          "content": null,
          "tool_calls": [
            { "id": "call_1", "type": "function",
              "function": { "name": "read_file", "arguments": "{\"path\":\"a.txt\"}" } },
            { "id": "call_2", "type": "function",
              "function": { "name": "write_file", "arguments": "{\"path\":\"b.txt\"}" } }
          ]
        },
        "finish_reason": "tool_calls"
      }],
      "usage": { "prompt_tokens": 30, "completion_tokens": 20 }
    }"""
    match parse json with
    | Ok { Body = WithToolCalls (None, nel) } ->
        Assert.Equal(ToolCallId "call_1", nel.Head.Id)
        let second = List.head nel.Tail
        Assert.Equal(ToolCallId "call_2", second.Id)
        Assert.Equal(ToolName "write_file", second.Tool)
    | other -> Assert.Fail($"Expected WithToolCalls with 2 calls, got {other}")

[<Fact>]
let ``text content alongside tool calls is captured in WithToolCalls Some`` () =
    // Some providers (e.g. Claude) emit thinking text + a tool call in one response.
    let json = """
    {
      "choices": [{
        "message": {
          "role": "assistant",
          "content": "Let me look that up.",
          "tool_calls": [
            { "id": "call_x", "type": "function",
              "function": { "name": "search", "arguments": "{\"q\":\"fsharp\"}" } }
          ]
        },
        "finish_reason": "tool_calls"
      }],
      "usage": { "prompt_tokens": 10, "completion_tokens": 8 }
    }"""
    match parse json with
    | Ok { Body = WithToolCalls (Some text, nel) } ->
        Assert.Equal("Let me look that up.", text)
        Assert.Equal(ToolCallId "call_x", nel.Head.Id)
    | other -> Assert.Fail($"Expected WithToolCalls(Some text, _), got {other}")

[<Fact>]
let ``cached_tokens in usage is captured correctly`` () =
    let json = """
    {
      "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 100, "completion_tokens": 20, "cached_tokens": 80 }
    }"""
    match parse json with
    | Ok { Usage = usage } -> Assert.Equal(80, usage.CachedTokens)
    | other -> Assert.Fail($"Expected Ok with cached_tokens=80, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Streaming chunk
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``content delta chunk parses to ContentDelta TextDelta`` () =
    let json = """
    { "choices": [{ "delta": { "content": "Hello" }, "finish_reason": null }] }"""
    match parseChunk json with
    | Ok (Some (ContentDelta (TextDelta "Hello"))) -> ()
    | other -> Assert.Fail($"Expected ContentDelta(TextDelta \"Hello\"), got {other}")

[<Fact>]
let ``empty content delta returns None`` () =
    let json = """
    { "choices": [{ "delta": { "content": "" }, "finish_reason": null }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None, got {other}")

[<Fact>]
let ``tool call start delta parses to ToolCallStarted`` () =
    let json = """
    { "choices": [{
        "delta": {
          "tool_calls": [{ "index": 0, "id": "call_abc",
                           "type": "function",
                           "function": { "name": "get_weather", "arguments": "" } }]
        },
        "finish_reason": null
    }] }"""
    match parseChunk json with
    | Ok (Some (ToolCallStarted (0, ToolCallId "call_abc", ToolName "get_weather"))) -> ()
    | other -> Assert.Fail($"Expected ToolCallStarted, got {other}")

[<Fact>]
let ``tool argument chunk parses to ContentDelta ToolArgDelta`` () =
    let json = """
    { "choices": [{
        "delta": {
          "tool_calls": [{ "index": 0,
                           "function": { "arguments": "{\"lo" } }]
        },
        "finish_reason": null
    }] }"""
    match parseChunk json with
    | Ok (Some (ContentDelta (ToolArgDelta (0, chunk)))) -> Assert.Equal("{\"lo", chunk)
    | other -> Assert.Fail($"Expected ToolArgDelta, got {other}")

[<Fact>]
let ``chunk with no choices returns None`` () =
    let json = """{ "id": "chatcmpl-123" }"""
    Assert.Equal(Ok None, parseChunk json)

[<Fact>]
let ``reasoning_content delta parses to ThinkingDelta`` () =
    let json = """
    { "choices": [{ "delta": { "reasoning_content": "I think..." },
                    "finish_reason": null }] }"""
    match parseChunk json with
    | Ok (Some (ContentDelta (ThinkingDelta "I think..."))) -> ()
    | other -> Assert.Fail($"Expected ThinkingDelta, got {other}")

[<Fact>]
let ``empty reasoning_content delta returns None`` () =
    let json = """
    { "choices": [{ "delta": { "reasoning_content": "" },
                    "finish_reason": null }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None, got {other}")

[<Fact>]
let ``tool call start with non-zero index emits correct index`` () =
    let json = """
    { "choices": [{
        "delta": {
          "tool_calls": [{ "index": 1, "id": "call_xyz", "type": "function",
                           "function": { "name": "search", "arguments": "" } }]
        },
        "finish_reason": null
    }] }"""
    match parseChunk json with
    | Ok (Some (ToolCallStarted (1, ToolCallId "call_xyz", ToolName "search"))) -> ()
    | other -> Assert.Fail($"Expected ToolCallStarted(1,...), got {other}")

// ── Streaming usage chunk (stream_options.include_usage=true) ────────────────

[<Fact>]
let ``final chunk with empty choices and usage emits StreamCompleted with token counts`` () =
    let json = """
    { "id": "chatcmpl-abc", "choices": [],
      "usage": { "prompt_tokens": 123, "completion_tokens": 45, "total_tokens": 168 } }"""
    match parseChunk json with
    | Ok (Some (StreamCompleted r)) ->
        Assert.Equal(123, r.Usage.PromptTokens)
        Assert.Equal(45,  r.Usage.CompletionTokens)
    | other -> Assert.Fail($"Expected StreamCompleted, got {other}")

[<Fact>]
let ``chunk with no choices key and usage emits StreamCompleted`` () =
    let json = """{ "usage": { "prompt_tokens": 10, "completion_tokens": 20 } }"""
    match parseChunk json with
    | Ok (Some (StreamCompleted r)) ->
        Assert.Equal(10, r.Usage.PromptTokens)
        Assert.Equal(20, r.Usage.CompletionTokens)
    | other -> Assert.Fail($"Expected StreamCompleted, got {other}")

[<Fact>]
let ``chunk with non-empty choices and usage does not emit StreamCompleted`` () =
    // Normal delta chunk — should NOT be mistaken for a usage chunk
    let json = """
    { "choices": [{ "delta": { "content": "hi" }, "finish_reason": null }],
      "usage": null }"""
    match parseChunk json with
    | Ok (Some (ContentDelta (TextDelta "hi"))) -> ()
    | other -> Assert.Fail($"Expected ContentDelta, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseToolCall — error paths
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseToolCall fails when id field is missing`` () =
    // Missing "id" → requireString "id" returns Error (MissingField "id")
    let json = """{"type":"function","function":{"name":"foo","arguments":"{}"}}"""
    use doc = JsonDocument.Parse(json)
    match parseToolCall doc.RootElement with
    | Error _ -> ()
    | Ok call -> Assert.Fail($"Expected Error for missing id, got {call}")

[<Fact>]
let ``parseToolCall fails when arguments is not valid JSON`` () =
    // arguments is not valid JSON → parseArguments returns Error (JsonParseError)
    let json = """{"id":"call_x","function":{"name":"foo","arguments":"not_json"}}"""
    use doc = JsonDocument.Parse(json)
    match parseToolCall doc.RootElement with
    | Error _ -> ()
    | Ok call -> Assert.Fail($"Expected Error for invalid arguments, got {call}")

// ═══════════════════════════════════════════════════════════════════════════
// parseLlmResponse — malformed tool call propagates error
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseLlmResponse fails when tool call entry is missing id`` () =
    // traverseResult parseToolCall fails → let! body = Error _ propagates out
    let json = """
    {
      "choices": [{
        "message": {
          "role": "assistant",
          "content": null,
          "tool_calls": [{"type":"function","function":{"name":"foo","arguments":"{}"}}]
        },
        "finish_reason": "tool_calls"
      }],
      "usage": { "prompt_tokens": 5, "completion_tokens": 5 }
    }"""
    match parse json with
    | Error _ -> ()
    | Ok v    -> Assert.Fail($"Expected Error for malformed tool call, got {v}")

// ═══════════════════════════════════════════════════════════════════════════
// parseStreamChunk — additional None branches
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``streaming chunk with no delta key in choice returns None`` () =
    // choices is non-empty but first choice has no "delta" key → return None
    let json = """
    { "choices": [{ "message": {"content": "x"}, "finish_reason": "stop" }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None for missing delta, got {other}")

[<Fact>]
let ``streaming chunk with empty tool_calls array in delta returns None`` () =
    let json = """
    { "choices": [{ "delta": { "tool_calls": [] }, "finish_reason": null }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None for empty tool_calls, got {other}")

[<Fact>]
let ``streaming chunk with empty string argument delta returns None`` () =
    // Empty-string argument chunk must be filtered out (same as empty content)
    let json = """
    { "choices": [{
        "delta": { "tool_calls": [{ "index": 0, "function": { "arguments": "" } }] },
        "finish_reason": null
    }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None for empty arg delta, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseArguments — non-object root
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseToolCall fails when arguments value is a JSON array not an object`` () =
    // parseArguments: if doc.RootElement.ValueKind <> JsonValueKind.Object → SchemaError
    let json = """{"id":"c1","function":{"name":"foo","arguments":"[]"}}"""
    use doc = JsonDocument.Parse(json)
    match parseToolCall doc.RootElement with
    | Error _ -> ()
    | Ok call -> Assert.Fail($"Expected Error for array arguments, got {call}")

// ═══════════════════════════════════════════════════════════════════════════
// parseStreamChunk — tool call delta with no function key
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``streaming tool call delta with no function key returns None`` () =
    // callEl.TryGetProperty("function") = (false, _) → return None at the false branch
    let json = """
    { "choices": [{
        "delta": { "tool_calls": [{ "index": 0, "id": "c1" }] },
        "finish_reason": null
    }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None for tool call delta with no function key, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryParseUsageChunk — prompt_tokens_details.cached_tokens
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``final usage chunk with prompt_tokens_details cached_tokens emits correct CachedTokens`` () =
    // Tests the nested prompt_tokens_details.cached_tokens path in tryParseUsageChunk
    let json = """
    { "choices": [],
      "usage": { "prompt_tokens": 100, "completion_tokens": 20,
                  "prompt_tokens_details": { "cached_tokens": 42 } } }"""
    match parseChunk json with
    | Ok (Some (StreamCompleted r)) -> Assert.Equal(42, r.Usage.CachedTokens)
    | other -> Assert.Fail($"Expected StreamCompleted with CachedTokens=42, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseLlmResponse — empty string content treated as Empty body
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseLlmResponse empty string content produces Empty body`` () =
    // content="" → | s when s = "" -> None → contentOpt = None → body = Empty
    let json = """
    { "choices": [{ "message": { "role": "assistant", "content": "" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 5, "completion_tokens": 0 } }"""
    match parse json with
    | Ok { Body = Empty } -> ()
    | other -> Assert.Fail($"Expected Empty for empty string content, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// tryParseUsageChunk — no usage key when no choices key → None
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``chunk with no choices key and no usage key returns None`` () =
    // tryParseUsageChunk: no choices → hasEmptyChoices=true; no usage → returns None
    // parseStreamChunk: choices absent, tryParseUsageChunk returns None → Ok None
    let json = """{ "id": "chatcmpl-123", "model": "gpt-4o" }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None when no choices and no usage, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseStreamChunk — delta with no recognized property returns None
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``streaming chunk with delta containing no recognized property returns None`` () =
    // delta has "role" but no content/reasoning_content/tool_calls → final | _ -> return None
    let json = """
    { "choices": [{ "delta": { "role": "assistant" }, "finish_reason": null }] }"""
    match parseChunk json with
    | Ok None -> ()
    | other -> Assert.Fail($"Expected None for delta with no recognized property, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseStreamChunk — tool call with null id produces empty-string callId
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``streaming ToolCallStarted with null id produces empty-string callId`` () =
    // id field present but string value is null → callId = ""
    let json = """
    { "choices": [{
        "delta": {
          "tool_calls": [{ "index": 0, "id": null,
                           "type": "function",
                           "function": { "name": "search", "arguments": "" } }]
        },
        "finish_reason": null
    }] }"""
    match parseChunk json with
    | Ok (Some (ToolCallStarted (0, ToolCallId "", ToolName "search"))) -> ()
    | other -> Assert.Fail($"Expected ToolCallStarted with empty callId, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseLlmResponse — reasoning_content null → None
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseLlmResponse with null reasoning_content produces None ReasoningContent`` () =
    // reasoning_content is present but JSON null → | null -> None
    let json = """
    { "choices": [{ "message": { "role": "assistant", "content": "hi",
                                  "reasoning_content": null },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 1, "completion_tokens": 1 } }"""
    match parse json with
    | Ok { ReasoningContent = None } -> ()
    | other -> Assert.Fail($"Expected None ReasoningContent for null value, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// FinishReason parsing — non-streaming
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseLlmResponse parses finish_reason stop as Some Stop`` () =
    let json = """
    { "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 1, "completion_tokens": 1 } }"""
    match parse json with
    | Ok { FinishReason = Some Stop } -> ()
    | other -> Assert.Fail($"Expected Some Stop, got {other}")

[<Fact>]
let ``parseLlmResponse parses finish_reason length as Some Length`` () =
    let json = """
    { "choices": [{ "message": { "content": "partial answer...", "role": "assistant" },
                    "finish_reason": "length" }],
      "usage": { "prompt_tokens": 1, "completion_tokens": 100 } }"""
    match parse json with
    | Ok { FinishReason = Some Length; Body = TextOnly "partial answer..." } -> ()
    | other -> Assert.Fail($"Expected Some Length with TextOnly, got {other}")

[<Fact>]
let ``parseLlmResponse parses finish_reason tool_calls as Some ToolCalls`` () =
    let json = """
    { "choices": [{ "message": { "content": null, "role": "assistant",
                                  "tool_calls": [{"id":"c1","type":"function","function":{"name":"foo","arguments":"{}"}}] },
                    "finish_reason": "tool_calls" }],
      "usage": { "prompt_tokens": 1, "completion_tokens": 5 } }"""
    match parse json with
    | Ok { FinishReason = Some ToolCalls } -> ()
    | other -> Assert.Fail($"Expected Some ToolCalls, got {other}")

[<Fact>]
let ``parseLlmResponse with null finish_reason produces None FinishReason`` () =
    let json = """
    { "choices": [{ "message": { "content": "ok", "role": "assistant" },
                    "finish_reason": null }],
      "usage": { "prompt_tokens": 1, "completion_tokens": 1 } }"""
    match parse json with
    | Ok { FinishReason = None } -> ()
    | other -> Assert.Fail($"Expected None FinishReason for null, got {other}")

[<Fact>]
let ``parseLlmResponse with unknown finish_reason produces Some OtherReason`` () =
    let json = """
    { "choices": [{ "message": { "content": "ok", "role": "assistant" },
                    "finish_reason": "content_moderation" }],
      "usage": { "prompt_tokens": 1, "completion_tokens": 1 } }"""
    match parse json with
    | Ok { FinishReason = Some (OtherReason "content_moderation") } -> ()
    | other -> Assert.Fail($"Expected Some (OtherReason \"content_moderation\"), got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// StreamFinished — stop chunk with finish_reason
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``stop chunk with finish_reason stop emits StreamFinished`` () =
    // The last meaningful streaming chunk: delta is empty but finish_reason is present.
    let json = """{"choices":[{"delta":{},"finish_reason":"stop","index":0}]}"""
    match parseChunk json with
    | Ok (Some (StreamFinished "stop")) -> ()
    | other -> Assert.Fail($"Expected StreamFinished \"stop\", got {other}")

[<Fact>]
let ``stop chunk with finish_reason length emits StreamFinished`` () =
    let json = """{"choices":[{"delta":{},"finish_reason":"length","index":0}]}"""
    match parseChunk json with
    | Ok (Some (StreamFinished "length")) -> ()
    | other -> Assert.Fail($"Expected StreamFinished \"length\", got {other}")

[<Fact>]
let ``content chunk with finish_reason null does not emit StreamFinished`` () =
    // A normal delta chunk has finish_reason null — should not emit StreamFinished.
    let json = """{"choices":[{"delta":{"content":"hello"},"finish_reason":null,"index":0}]}"""
    match parseChunk json with
    | Ok (Some (ContentDelta (TextDelta "hello"))) -> ()
    | other -> Assert.Fail($"Expected ContentDelta TextDelta, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// parseUsage — cached token variants
// Python parity: tests/providers/test_cached_tokens.py
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``parseUsage extracts nested prompt_tokens_details cached_tokens (OpenAI format)`` () =
    // Python parity: test_extract_usage_openai_cached_tokens_dict
    let json = """
    { "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 2000, "completion_tokens": 300,
                  "prompt_tokens_details": { "cached_tokens": 1200 } } }"""
    match parse json with
    | Ok { Usage = u } ->
        Assert.Equal(2000, u.PromptTokens)
        Assert.Equal(1200, u.CachedTokens)
    | other -> Assert.Fail($"Expected Ok with nested cached_tokens, got {other}")

[<Fact>]
let ``parseUsage extracts prompt_cache_hit_tokens (DeepSeek format)`` () =
    // Python parity: test_extract_usage_deepseek_cached_tokens_dict
    let json = """
    { "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 1500, "completion_tokens": 200,
                  "prompt_cache_hit_tokens": 1200 } }"""
    match parse json with
    | Ok { Usage = u } -> Assert.Equal(1200, u.CachedTokens)
    | other -> Assert.Fail($"Expected Ok with DeepSeek cached_tokens, got {other}")

[<Fact>]
let ``parseUsage zero nested cached_tokens falls back to top-level cached_tokens`` () =
    // Python parity: test_extract_usage_openai_cached_zero_dict —
    // F# priority: nested path returns 0, so falls through to top-level field.
    // Here: nested=0, top-level absent → CachedTokens should be 0.
    let json = """
    { "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 2000, "completion_tokens": 300,
                  "prompt_tokens_details": { "cached_tokens": 0 } } }"""
    match parse json with
    | Ok { Usage = u } -> Assert.Equal(0, u.CachedTokens)
    | other -> Assert.Fail($"Expected CachedTokens=0 when nested=0, got {other}")

[<Fact>]
let ``parseUsage nested prompt_tokens_details takes priority over top-level cached_tokens`` () =
    // Python parity: test_extract_usage_priority_nested_over_top_level_dict
    let json = """
    { "choices": [{ "message": { "content": "hi", "role": "assistant" },
                    "finish_reason": "stop" }],
      "usage": { "prompt_tokens": 2000, "completion_tokens": 300,
                  "prompt_tokens_details": { "cached_tokens": 100 },
                  "cached_tokens": 500 } }"""
    match parse json with
    | Ok { Usage = u } -> Assert.Equal(100, u.CachedTokens)
    | other -> Assert.Fail($"Expected nested cached_tokens=100 to win, got {other}")

// ── StepFun reasoning field fallback (Python parity: test_stepfun_reasoning.py) ─

[<Fact>]
let ``parseLlmResponse StepFun reasoning field used as content fallback when content is null`` () =
    // Python parity: test_parse_dict_stepfun_reasoning_fallback
    // When content is null and reasoning exists, content uses reasoning value.
    let json = """
    { "choices": [{ "message": { "content": null, "reasoning": "Let me think... The answer is 42." },
                    "finish_reason": "stop" }] }"""
    match parse json with
    | Ok resp ->
        match resp.Body with
        | TextOnly text -> Assert.Equal("Let me think... The answer is 42.", text)
        | other -> Assert.Fail($"Expected TextOnly, got {other}")
        Assert.Equal(Some "Let me think... The answer is 42.", resp.ReasoningContent)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``parseLlmResponse reasoning_content takes priority over reasoning for ReasoningContent`` () =
    // Python parity: test_parse_dict_stepfun_reasoning_priority
    // When both reasoning and reasoning_content exist:
    // - content falls back to reasoning (since content is null)
    // - ReasoningContent uses reasoning_content (takes priority)
    let json = """
    { "choices": [{ "message": { "content": null,
                                  "reasoning": "informal thinking",
                                  "reasoning_content": "formal reasoning content" },
                    "finish_reason": "stop" }] }"""
    match parse json with
    | Ok resp ->
        match resp.Body with
        | TextOnly text -> Assert.Equal("informal thinking", text)
        | other -> Assert.Fail($"Expected TextOnly from reasoning fallback, got {other}")
        Assert.Equal(Some "formal reasoning content", resp.ReasoningContent)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``parseLlmResponse normal model with reasoning_content and content is unaffected`` () =
    // Python parity: test_parse_dict_normal_model_with_reasoning_content_unaffected
    // Models that return both content and reasoning_content (e.g. DeepSeek-R1) should
    // not be affected by the StepFun reasoning fallback.
    let json = """
    { "choices": [{ "message": { "content": "The answer is 42.",
                                  "reasoning_content": "Let me think step by step..." },
                    "finish_reason": "stop" }] }"""
    match parse json with
    | Ok resp ->
        match resp.Body with
        | TextOnly text -> Assert.Equal("The answer is 42.", text)
        | other -> Assert.Fail($"Expected TextOnly with original content, got {other}")
        Assert.Equal(Some "Let me think step by step...", resp.ReasoningContent)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``parseLlmResponse standard model with no reasoning fields is unaffected`` () =
    // Python parity: test_parse_dict_standard_model_no_reasoning_unaffected
    let json = """
    { "choices": [{ "message": { "content": "Hello!" },
                    "finish_reason": "stop" }] }"""
    match parse json with
    | Ok resp ->
        match resp.Body with
        | TextOnly text -> Assert.Equal("Hello!", text)
        | other -> Assert.Fail($"Expected TextOnly, got {other}")
        Assert.Equal(None, resp.ReasoningContent)
    | other -> Assert.Fail($"Expected Ok, got {other}")

[<Fact>]
let ``parseStreamChunk StepFun reasoning delta emits ThinkingDelta`` () =
    // Python parity: test_parse_chunks_dict_stepfun_reasoning_fallback
    // When reasoning_content is absent, reasoning delta maps to ThinkingDelta.
    let json = """{ "choices": [{ "delta": { "reasoning": "Thinking step 1..." },
                                   "finish_reason": null }] }"""
    match parseChunk json with
    | Ok (Some (ContentDelta (ThinkingDelta s))) -> Assert.Equal("Thinking step 1...", s)
    | other -> Assert.Fail($"Expected ThinkingDelta from reasoning field, got {other}")

[<Fact>]
let ``parseStreamChunk reasoning_content delta takes priority over reasoning`` () =
    // Python parity: test_parse_chunks_dict_reasoning_precedence
    // When both reasoning_content and reasoning are present, reasoning_content wins.
    let json = """{ "choices": [{ "delta": { "reasoning_content": "formal: ",
                                              "reasoning": "informal: " },
                                   "finish_reason": null }] }"""
    match parseChunk json with
    | Ok (Some (ContentDelta (ThinkingDelta s))) -> Assert.Equal("formal: ", s)
    | other -> Assert.Fail($"Expected ThinkingDelta from reasoning_content (takes priority), got {other}")
