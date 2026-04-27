module BotSharp.Tests.Parsers.SseParserTests

open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Providers.SseParser

// ═══════════════════════════════════════════════════════════════════════════
// SSE line parser
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``"data: [DONE]" parses to DoneLine`` () =
    Assert.Equal(Ok DoneLine, parseSseLine "data: [DONE]")

[<Fact>]
let ``"data:[DONE]" (no space) parses to DoneLine`` () =
    Assert.Equal(Ok DoneLine, parseSseLine "data:[DONE]")

[<Fact>]
let ``data line with JSON payload parses to DataLine`` () =
    let json = """{"choices":[{"delta":{"content":"hello"}}]}"""
    match parseSseLine $"data: {json}" with
    | Ok (DataLine payload) -> Assert.Equal(json, payload)
    | other -> Assert.Fail($"Expected DataLine, got {other}")

[<Fact>]
let ``comment line (colon prefix) parses to CommentLine`` () =
    Assert.Equal(Ok CommentLine, parseSseLine ": heartbeat")
    Assert.Equal(Ok CommentLine, parseSseLine ": ")
    Assert.Equal(Ok CommentLine, parseSseLine ":")

[<Fact>]
let ``event line parses to CommentLine`` () =
    Assert.Equal(Ok CommentLine, parseSseLine "event: message")

[<Fact>]
let ``empty line parses to CommentLine`` () =
    Assert.Equal(Ok CommentLine, parseSseLine "")

[<Fact>]
let ``data line without space after colon still parses`` () =
    match parseSseLine "data:{}" with
    | Ok (DataLine "{}") -> ()
    | other -> Assert.Fail($"Expected DataLine \"{{}}\", got {other}")

[<Fact>]
let ``DoneLine takes precedence over DataLine`` () =
    // "data: [DONE]" must be DoneLine, not DataLine "[DONE]"
    match parseSseLine "data: [DONE]" with
    | Ok DoneLine -> ()
    | other -> Assert.Fail($"Expected DoneLine, got {other}")

[<Fact>]
let ``id: field parses to CommentLine (event ID is ignored)`` () =
    Assert.Equal(Ok CommentLine, parseSseLine "id: 42")

[<Fact>]
let ``id: field with no value parses to CommentLine`` () =
    Assert.Equal(Ok CommentLine, parseSseLine "id:")

[<Fact>]
let ``retry: field parses to CommentLine (reconnect hint is ignored)`` () =
    Assert.Equal(Ok CommentLine, parseSseLine "retry: 5000")

[<Fact>]
let ``retry: field with no value parses to CommentLine`` () =
    Assert.Equal(Ok CommentLine, parseSseLine "retry:")

[<Fact>]
let ``data line with Unicode content parses correctly`` () =
    let payload = """{"content":"你好世界 🌍"}"""
    match parseSseLine $"data: {payload}" with
    | Ok (DataLine p) -> Assert.Equal(payload, p)
    | other -> Assert.Fail($"Expected DataLine, got {other}")

[<Fact>]
let ``completely unrecognized line prefix returns Error`` () =
    // "unknown: value" doesn't start with data:, :, event:, id:, retry:, or eof.
    // All parsers fail → parseSseLine returns Error.
    match parseSseLine "unknown: value" with
    | Error _ -> ()   // any ParseError is acceptable
    | Ok frame -> Assert.Fail($"Expected Error for unrecognized prefix, got {frame}")

[<Fact>]
let ``data: with no payload parses to DataLine with empty string`` () =
    // "data:" — after pstring "data:" and spaces, manyChars anyChar yields ""
    match parseSseLine "data:" with
    | Ok (DataLine "") -> ()
    | other -> Assert.Fail($"Expected DataLine \"\", got {other}")

[<Fact>]
let ``data line with only spaces after colon parses to DataLine with empty string`` () =
    // "data:   " — spaces are consumed by `spaces`, manyChars anyChar yields ""
    match parseSseLine "data:   " with
    | Ok (DataLine "") -> ()
    | other -> Assert.Fail($"Expected DataLine \"\", got {other}")

[<Fact>]
let ``whitespace-only line that is not empty returns Error`` () =
    // "   " has no valid prefix (not data:, :, event:, id:, retry:) and is not eof.
    // The eof alternative only matches when the parser position is at end of input
    // at the start, which is the empty-string case.
    match parseSseLine "   " with
    | Error _ -> ()
    | Ok frame -> Assert.Fail($"Expected Error for whitespace-only line, got {frame}")

[<Fact>]
let ``event: with no value after colon parses to CommentLine`` () =
    // pstring "event:" >>. skipRestOfLine true — empty remaining → CommentLine
    Assert.Equal(Ok CommentLine, parseSseLine "event:")

[<Fact>]
let ``id: with only spaces parses to CommentLine`` () =
    // skipRestOfLine consumes the trailing whitespace → CommentLine
    Assert.Equal(Ok CommentLine, parseSseLine "id:   ")
