module BotSharp.Tests.Infrastructure.MessageToolTests

open System.Text.Json
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.MessageTool

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private jsonStr (s: string) =
    JsonDocument.Parse($"\"{s}\"").RootElement.Clone()

let private jsonStringArray (items: string list) =
    let body = items |> List.map (fun s -> $"\"{s}\"") |> String.concat ","
    JsonDocument.Parse($"[{body}]").RootElement.Clone()

let private makeArgs (pairs: (string * JsonElement) list) : Map<string, JsonElement> =
    pairs |> Map.ofList

// ═══════════════════════════════════════════════════════════════════════════
// Basic send
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeMessage sends message with correct content`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "hello user" ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess _ ->
        Assert.True(sent.IsSome, "send callback should have been called")
        Assert.Equal("hello user", sent.Value.Content)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``executeMessage returns ToolFailure when content is missing`` () =
    let send _ = async { () }
    let args = Map.empty
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for missing content, got {other}")

[<Fact>]
let ``executeMessage uses default channel and chat when not specified`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "hi" ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    let (ChannelId ch) = sent.Value.Channel
    let (ChatId ct)    = sent.Value.Chat
    Assert.Equal("cli", ch)
    Assert.Equal("cli-session", ct)

// ═══════════════════════════════════════════════════════════════════════════
// Media parameter
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeMessage without media sends empty Attachments`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "no files" ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    Assert.Empty(sent.Value.Attachments)

[<Fact>]
let ``executeMessage with image media sends ImageFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "see attached"
        "media",   jsonStringArray ["/tmp/photo.jpg"]
    ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("1 attachment", msg)
        Assert.True(sent.IsSome)
        match sent.Value.Attachments with
        | [ ImageFile lp ] -> Assert.Equal("/tmp/photo.jpg", LocalFilePath.value lp)
        | other -> Assert.Fail($"Expected [ImageFile], got {other}")
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``executeMessage with document media sends DocumentFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "report"
        "media",   jsonStringArray ["/tmp/report.pdf"]
    ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    match sent.Value.Attachments with
    | [ DocumentFile _ ] -> ()
    | other -> Assert.Fail($"Expected [DocumentFile], got {other}")

[<Fact>]
let ``executeMessage with audio media sends AudioFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "audio"
        "media",   jsonStringArray ["/tmp/clip.mp3"]
    ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    match sent.Value.Attachments with
    | [ AudioFile _ ] -> ()
    | other -> Assert.Fail($"Expected [AudioFile], got {other}")

[<Fact>]
let ``executeMessage with multiple media creates multiple attachments`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "multi"
        "media",   jsonStringArray ["/tmp/a.jpg"; "/tmp/b.pdf"]
    ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("2 attachment", msg)
        Assert.Equal(2, sent.Value.Attachments.Length)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Buttons parameter
// ═══════════════════════════════════════════════════════════════════════════

let private jsonNestedStringArray (rows: string list list) =
    let rowJson (row: string list) =
        let items = row |> List.map (fun s -> $"\"{s}\"") |> String.concat ","
        $"[{items}]"
    let body = rows |> List.map rowJson |> String.concat ","
    JsonDocument.Parse($"[{body}]").RootElement.Clone()

[<Fact>]
let ``executeMessage without buttons sends empty Buttons`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "no buttons" ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    Assert.Empty(sent.Value.Buttons)

[<Fact>]
let ``executeMessage with buttons populates Buttons field`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "pick one"
        "buttons", jsonNestedStringArray [["Yes"; "No"]; ["Cancel"]]
    ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("3 button", msg)
        Assert.True(sent.IsSome)
        Assert.Equal(2, sent.Value.Buttons.Length)
        Assert.Equal<string list>(["Yes"; "No"], sent.Value.Buttons.[0])
        Assert.Equal<string list>(["Cancel"], sent.Value.Buttons.[1])
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``executeMessage returns ToolFailure for invalid buttons structure`` () =
    let send _ = async { () }
    let notAnArray = JsonDocument.Parse("\"not-an-array\"").RootElement.Clone()
    let args = makeArgs [ "content", jsonStr "bad"; "buttons", notAnArray ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | other -> Assert.Fail($"Expected ToolFailure for invalid buttons, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Media — VideoFile classification
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeMessage with video media sends VideoFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "watch this"
        "media",   jsonStringArray ["/tmp/demo.mp4"]
    ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    match sent.Value.Attachments with
    | [ VideoFile _ ] -> ()
    | other -> Assert.Fail($"Expected [VideoFile], got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// Custom channel and chat routing
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeMessage with explicit channel and chat routes there`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "routed"
        "channel", jsonStr "telegram"
        "chat",    jsonStr "987654"
    ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.True(sent.IsSome)
    Assert.Equal(ChannelId "telegram", sent.Value.Channel)
    Assert.Equal(ChatId "987654",      sent.Value.Chat)

// ═══════════════════════════════════════════════════════════════════════════
// Send exception → ToolFailure
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeMessage returns ToolFailure when send callback throws`` () =
    let failingSend : OutboundMessage -> Async<unit> =
        fun _ -> async { failwith "network timeout" }
    let args = makeArgs [ "content", jsonStr "will fail" ]
    let result = executeMessage failingSend args |> Async.RunSynchronously
    match result with
    | ToolFailure (ExecutionFailed msg) -> Assert.Contains("network timeout", msg)
    | other -> Assert.Fail($"Expected ToolFailure(ExecutionFailed), got {other}")

[<Fact>]
let ``executeMessage with unknown extension sends DocumentFile attachment`` () =
    // Extension ".xyz" doesn't match image/audio/video → falls through to DocumentFile
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let unknownPath = JsonDocument.Parse("\"/tmp/file.xyz\"").RootElement.Clone()
    let mediaArr =
        let body = $"[\"/tmp/file.xyz\"]"
        JsonDocument.Parse(body).RootElement.Clone()
    let args = makeArgs [ "content", jsonStr "generic"; "media", mediaArr ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess _ ->
        Assert.True(sent.IsSome, "Expected OutboundMessage to be sent")
        match sent.Value.Attachments with
        | [ DocumentFile _ ] -> ()
        | other -> Assert.Fail($"Expected DocumentFile attachment, got {other}")
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``executeMessage with buttons row containing non-string element returns ToolFailure`` () =
    // A row that is an array but contains a number → ParameterInvalid
    let send _ = async { () }
    let badButtons = JsonDocument.Parse("[[1, 2, 3]]").RootElement.Clone()
    let args = makeArgs [ "content", jsonStr "bad buttons"; "buttons", badButtons ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolFailure (ParameterInvalid ("buttons", _)) -> ()
    | other -> Assert.Fail($"Expected ToolFailure(ParameterInvalid buttons), got {other}")

[<Fact>]
let ``executeMessage with non-array row in buttons returns ToolFailure`` () =
    // [[Yes], "not-a-row"] — the second row is a string, not a nested array
    let send _ = async { () }
    let mixedButtons = JsonDocument.Parse("""[["Yes"],"not-a-row"]""").RootElement.Clone()
    let args = makeArgs [ "content", jsonStr "mixed"; "buttons", mixedButtons ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolFailure (ParameterInvalid ("buttons", _)) -> ()
    | other -> Assert.Fail($"Expected ToolFailure(ParameterInvalid buttons) for non-array row, got {other}")

[<Fact>]
let ``executeMessage with both media and buttons includes both counts in success message`` () =
    // Tests the combined mediaInfo + buttonInfo path in the success message
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [
        "content", jsonStr "full message"
        "media",   jsonStringArray ["/tmp/a.jpg"; "/tmp/b.pdf"]
        "buttons", jsonNestedStringArray [["OK"; "Cancel"]]
    ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg ->
        Assert.Contains("2 attachment", msg)
        Assert.Contains("2 button",    msg)
        Assert.Equal(2, sent.Value.Attachments.Length)
        Assert.Equal(1, sent.Value.Buttons.Length)
    | other -> Assert.Fail($"Expected ToolSuccess with combined summary, got {other}")

[<Fact>]
let ``allTools returns exactly 1 message tool`` () =
    let tools = allTools (fun _ -> async { () })
    Assert.Equal(1, tools.Length)
    let (spec, _) = tools.[0]
    let (ToolName n) = spec.Name
    Assert.Equal("message", n)

[<Fact>]
let ``executeMessage success result contains 'Message delivered to'`` () =
    let send _ = async { () }
    let args = makeArgs [ "content", jsonStr "hi" ]
    let result = executeMessage send args |> Async.RunSynchronously
    match result with
    | ToolSuccess msg -> Assert.Contains("Message delivered to", msg)
    | other -> Assert.Fail($"Expected ToolSuccess, got {other}")

[<Fact>]
let ``executeMessage with png extension sends ImageFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "image"; "media", jsonStringArray ["/tmp/shot.png"] ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    match sent.Value.Attachments with
    | [ ImageFile _ ] -> ()
    | other -> Assert.Fail($"Expected ImageFile for .png, got {other}")

[<Fact>]
let ``executeMessage with wav extension sends AudioFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "audio"; "media", jsonStringArray ["/tmp/clip.wav"] ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    match sent.Value.Attachments with
    | [ AudioFile _ ] -> ()
    | other -> Assert.Fail($"Expected AudioFile for .wav, got {other}")

[<Fact>]
let ``executeMessage with mov extension sends VideoFile attachment`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "video"; "media", jsonStringArray ["/tmp/clip.mov"] ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    match sent.Value.Attachments with
    | [ VideoFile _ ] -> ()
    | other -> Assert.Fail($"Expected VideoFile for .mov, got {other}")

// ═══════════════════════════════════════════════════════════════════════════
// executeMessage — strip_think parity (Python mirrors)
// Python's message.execute calls strip_think(content) before sending.
// F# must do the same via executeMessage → stripThink.
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``executeMessage strips well-formed think block from content`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let content = "<think>My internal reasoning.</think>Here is the answer."
    let args = makeArgs [ "content", jsonStr content ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    // The think block must be stripped; only the answer should arrive
    Assert.Equal("Here is the answer.", sent.Value.Content)

[<Fact>]
let ``executeMessage preserves plain content without think tags`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let args = makeArgs [ "content", jsonStr "Simple message." ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    Assert.Equal("Simple message.", sent.Value.Content)

[<Fact>]
let ``executeMessage strips unclosed think block`` () =
    let mutable sent : OutboundMessage option = None
    let send msg = async { sent <- Some msg }
    let content = "<think>Streaming reasoning that never closed..."
    let args = makeArgs [ "content", jsonStr content ]
    executeMessage send args |> Async.RunSynchronously |> ignore
    // An unclosed <think> block should result in empty/whitespace-only content
    Assert.True(sent.Value.Content.Trim() = "",
        $"Expected empty content after stripping unclosed think, got '{sent.Value.Content}'")
