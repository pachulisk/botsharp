module BotSharp.Infrastructure.Providers.LlmResponseParser

open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.Json

// ═══════════════════════════════════════════════════════════════════════════
// OpenAI-compat JSON response decoder
//
// Non-streaming format (complete response):
// {
//   "choices": [{ "message": { "content": "...", "tool_calls": [...] },
//                 "finish_reason": "stop" }],
//   "usage": { "prompt_tokens": N, "completion_tokens": M,
//               "cached_tokens": K }
// }
//
// Streaming chunk format (one SSE data line):
// {
//   "choices": [{ "delta": { "content": "...", "tool_calls": [...] },
//                 "finish_reason": null }]
// }
// ═══════════════════════════════════════════════════════════════════════════

// ── Argument parsing ──────────────────────────────────────────────────────

/// Parse the function.arguments JSON string into Map<string, JsonElement>.
/// Clones each element so the result is independent of the document lifetime.
let private parseArguments (raw: string) : Result<Map<string, JsonElement>, ParseError> =
    try
        use doc = JsonDocument.Parse(raw)
        if doc.RootElement.ValueKind <> JsonValueKind.Object then
            Error (SchemaError ("arguments", $"expected JSON object, got {doc.RootElement.ValueKind}"))
        else
            let map =
                doc.RootElement.EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.Clone())
                |> Map.ofSeq
            Ok map
    with ex ->
        Error (JsonParseError (ex.Message, 0))

// ── Tool-call parsing ─────────────────────────────────────────────────────

/// Parse a single tool-call object from an OpenAI-compat response.
/// {
///   "id": "call_abc",
///   "type": "function",
///   "function": { "name": "...", "arguments": "{...}" }
/// }
let parseToolCall (el: JsonElement) : Result<ToolCall, ParseError> =
    result {
        let! id       = requireString "id" el
        let! fnObj    = requireObject "function" el
        let! name     = requireString "name" fnObj
        let! argsRaw  = requireString "arguments" fnObj
        let! args     = parseArguments argsRaw
        return {
            Id           = ToolCallId id
            Tool         = ToolName name
            Arguments    = args
            ProviderMeta = None
        }
    }

// ── Usage parsing ─────────────────────────────────────────────────────────

let private parseUsage (el: JsonElement) : TokenUsage =
    // Cached-token extraction follows provider priority (Python parity: _extract_usage):
    //   1. prompt_tokens_details.cached_tokens  (OpenAI nested format)
    //   2. prompt_cache_hit_tokens              (DeepSeek)
    //   3. cached_tokens                        (StepFun/Moonshot top-level)
    let cachedTokens =
        match el.TryGetProperty("prompt_tokens_details") with
        | true, d ->
            match d.TryGetProperty("cached_tokens") with
            | true, c when c.ValueKind = JsonValueKind.Number && c.GetInt32() > 0 -> c.GetInt32()
            | _ -> 0
        | _ ->
            match tryGetInt "prompt_cache_hit_tokens" el with
            | Some v when v > 0 -> v
            | _ -> tryGetInt "cached_tokens" el |> Option.defaultValue 0
    { PromptTokens     = tryGetInt "prompt_tokens"     el |> Option.defaultValue 0
      CompletionTokens = tryGetInt "completion_tokens" el |> Option.defaultValue 0
      CachedTokens     = cachedTokens }

// ── Non-streaming complete response ──────────────────────────────────────

/// Parse a complete (non-streaming) OpenAI-compat response JSON element.
let parseLlmResponse (el: JsonElement) : Result<LLMResponse, ParseError> =
    result {
        let! choices = requireArray "choices" el
        if choices.IsEmpty then
            return {
                Body = Empty
                ReasoningContent = None
                ThinkingBlocks = []
                Usage = { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }
                FinishReason = None
            }
        else
            let first = List.head choices
            let! msg  = requireObject "message" first

            // content is a nullable string in OpenAI format
            let rawContentOpt =
                match msg.TryGetProperty("content") with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    match v.GetString() with
                    | null -> None
                    | s when s = "" -> None
                    | s -> Some s
                | _ -> None

            // reasoning_content (DeepSeek-R1 / some providers)
            let rawReasoningOpt =
                match msg.TryGetProperty("reasoning_content") with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    match v.GetString() with
                    | null -> None
                    | s    -> Some s
                | _ -> None

            // StepFun Plan API: `reasoning` field — used as content fallback when content is null,
            // and as reasoning_content fallback when reasoning_content is absent.
            // Python parity: _parse / _parse_chunks in openai_compat_provider.py.
            let stepFunReasoningOpt =
                match msg.TryGetProperty("reasoning") with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    match v.GetString() with
                    | null | "" -> None
                    | s         -> Some s
                | _ -> None

            let contentOpt   = rawContentOpt   |> Option.orElse stepFunReasoningOpt
            let reasoningOpt = rawReasoningOpt |> Option.orElse stepFunReasoningOpt

            let toolCallsOpt =
                match msg.TryGetProperty("tool_calls") with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    Some (v.EnumerateArray() |> Seq.cast<JsonElement> |> Seq.toList)
                | _ -> None

            let usage =
                match el.TryGetProperty("usage") with
                | true, u -> parseUsage u
                | _       -> { PromptTokens = 0; CompletionTokens = 0; CachedTokens = 0 }

            // finish_reason from the first choice (OpenAI: "stop", "length", "tool_calls", etc.)
            let finishReason =
                match first.TryGetProperty("finish_reason") with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    match v.GetString() with
                    | null          -> None
                    | "stop"        -> Some Stop
                    | "length"      -> Some Length
                    | "tool_calls"  -> Some ToolCalls
                    | "content_filter" -> Some ContentFilter
                    | other         -> Some (OtherReason other)
                | _ -> None

            let! body =
                match toolCallsOpt with
                | Some callEls when not callEls.IsEmpty ->
                    traverseResult parseToolCall callEls
                    |> Result.bind (fun calls ->
                        match NonEmptyList.ofList calls with
                        | Ok nel  -> Ok (WithToolCalls (contentOpt, nel))
                        | Error _ -> Ok Empty)  // guard: empty call list shouldn't reach here but degrade safely
                | _ ->
                    match contentOpt with
                    | Some c -> Ok (TextOnly c)
                    | None   -> Ok Empty

            return {
                Body             = body
                ReasoningContent = reasoningOpt
                ThinkingBlocks   = []
                Usage            = usage
                FinishReason     = finishReason
            }
    }

// ── Streaming chunk ───────────────────────────────────────────────────────

/// Parse one SSE data-line JSON element into a StreamEvent.
/// Returns None for empty deltas or final bookkeeping chunks.
/// Parse the final usage-only chunk emitted when stream_options.include_usage=true.
/// Format: {"choices":[],"usage":{"prompt_tokens":N,"completion_tokens":N,...}}
///         or    {"usage":{"prompt_tokens":N,...}} (no choices key at all)
let private tryParseUsageChunk (el: JsonElement) : StreamEvent option =
    // Emit StreamCompleted only when usage is present AND choices are empty/absent.
    let hasEmptyChoices =
        match el.TryGetProperty("choices") with
        | true, v ->
            v.ValueKind = JsonValueKind.Array &&
            (let mutable en = v.EnumerateArray() in not (en.MoveNext()))
        | false, _ -> true  // no choices key → treat as usage-only
    if not hasEmptyChoices then None
    else
        match el.TryGetProperty("usage") with
        | false, _ -> None
        | true, u  ->
            let usage = { PromptTokens     = (match u.TryGetProperty("prompt_tokens")     with true, v -> v.GetInt32() | _ -> 0)
                          CompletionTokens = (match u.TryGetProperty("completion_tokens")  with true, v -> v.GetInt32() | _ -> 0)
                          CachedTokens     = (match u.TryGetProperty("prompt_tokens_details") with
                                              | true, d ->
                                                  match d.TryGetProperty("cached_tokens") with
                                                  | true, c -> c.GetInt32()
                                                  | _       -> 0
                                              | _ -> 0) }
            let synth = { Body = Empty; ReasoningContent = None; ThinkingBlocks = []; Usage = usage; FinishReason = None }
            Some (StreamCompleted synth)

let parseStreamChunk (el: JsonElement) : Result<StreamEvent option, ParseError> =
    result {
        match el.TryGetProperty("choices") with
        | false, _ ->
            // No choices key — could be a usage-only final chunk.
            return tryParseUsageChunk el
        | true, choicesEl ->
            let choices = choicesEl.EnumerateArray() |> Seq.cast<JsonElement> |> Seq.toList
            if choices.IsEmpty then
                // Empty choices array — final usage chunk with stream_options.include_usage.
                return tryParseUsageChunk el
            else
                let first = List.head choices
                match first.TryGetProperty("delta") with
                | false, _ -> return None
                | true, delta ->

                    // Content delta (plain text)
                    match delta.TryGetProperty("content") with
                    | true, v when v.ValueKind = JsonValueKind.String ->
                        match v.GetString() with
                        | null | "" -> return None
                        | s         -> return Some (ContentDelta (TextDelta s))
                    | _ ->

                    // Reasoning / thinking delta (DeepSeek-R1 / extended thinking models)
                    match delta.TryGetProperty("reasoning_content") with
                    | true, v when v.ValueKind = JsonValueKind.String ->
                        match v.GetString() with
                        | null | "" -> return None
                        | s         -> return Some (ContentDelta (ThinkingDelta s))
                    | _ ->

                    // StepFun Plan API: `reasoning` delta used as ThinkingDelta fallback
                    // when reasoning_content is absent. Python parity: _parse_chunks.
                    match delta.TryGetProperty("reasoning") with
                    | true, v when v.ValueKind = JsonValueKind.String ->
                        match v.GetString() with
                        | null | "" -> return None
                        | s         -> return Some (ContentDelta (ThinkingDelta s))
                    | _ ->

                    // Tool-call delta
                    match delta.TryGetProperty("tool_calls") with
                    | true, tc when tc.ValueKind = JsonValueKind.Array ->
                        let calls = tc.EnumerateArray() |> Seq.cast<JsonElement> |> Seq.toList
                        if calls.IsEmpty then return None
                        else
                            let callEl = List.head calls
                            let idx =
                                match callEl.TryGetProperty("index") with
                                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                                | _ -> 0
                            match callEl.TryGetProperty("function") with
                            | false, _ -> return None
                            | true, fnEl ->
                                match fnEl.TryGetProperty("name") with
                                | true, nameEl when nameEl.ValueKind = JsonValueKind.String ->
                                    match nameEl.GetString() with
                                    | null -> return None
                                    | name ->
                                        let callId =
                                            match callEl.TryGetProperty("id") with
                                            | true, v when v.ValueKind = JsonValueKind.String ->
                                                match v.GetString() with
                                                | null -> ""
                                                | s    -> s
                                            | _ -> ""
                                        return Some (ToolCallStarted (idx, ToolCallId callId, ToolName name))
                                | _ ->
                                    // Argument chunk for an in-progress tool call
                                    match fnEl.TryGetProperty("arguments") with
                                    | true, argEl when argEl.ValueKind = JsonValueKind.String ->
                                        match argEl.GetString() with
                                        | null | "" -> return None
                                        | chunk     -> return Some (ContentDelta (ToolArgDelta (idx, chunk)))
                                    | _ -> return None
                    | _ ->
                    // No delta content — check for stop chunk (finish_reason without content).
                    // Emitted by provider as the last meaningful chunk before the usage chunk.
                    match first.TryGetProperty("finish_reason") with
                    | true, v when v.ValueKind = JsonValueKind.String ->
                        match v.GetString() with
                        | null | "" -> return None
                        | reason    -> return Some (StreamFinished reason)
                    | _ -> return None
    }
