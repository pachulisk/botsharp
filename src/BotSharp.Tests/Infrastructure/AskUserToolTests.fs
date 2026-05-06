module BotSharp.Tests.Infrastructure.AskUserToolTests

open System
open System.Text.Json
open System.Threading
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Tools.AskUserTool

// ═══════════════════════════════════════════════════════════════════════════
// AskUserTool tests
//
// matchOption and parseOptions are private; tested indirectly via
// executeAskUser with stub callbacks.  For TCS-based tests, the
// ManualResetEventSlim pattern ensures registerPending has been called
// before we complete the TCS.
// ═══════════════════════════════════════════════════════════════════════════

/// Build a Map<string, JsonElement> from a JSON object string.
let private parseArgs (json: string) : Map<string, JsonElement> =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.EnumerateObject()
    |> Seq.map (fun p -> p.Name, p.Value.Clone())
    |> Map.ofSeq

/// Run executeAskUser with minimal stub callbacks.
/// Returns a tuple of (task, storedQuery mutable ref, registered event).
let private runAskUser (args: Map<string, JsonElement>) =
    let registered  = new ManualResetEventSlim(false)
    let storedQuery = ref Option<PendingUserQuery>.None
    let registerPending _ (q: PendingUserQuery) =
        storedQuery.Value <- Some q
        registered.Set()
    let removePending _ = ()
    let send _  = async { return () }
    let getSid  = fun () -> SessionId "test"
    let getChan = fun () -> ChannelId "cli"
    let getChat = fun () -> ChatId "chat1"
    let task =
        executeAskUser registerPending removePending send getSid getChan getChat args
        |> Async.StartAsTask
    (task, storedQuery, registered)

// ── Argument validation ──────────────────────────────────────────────────

[<Fact>]
let ``executeAskUser returns ToolFailure when question is missing`` () =
    let args = parseArgs """{"options":["Yes","No"]}"""
    let result = executeAskUser (fun _ _ -> ()) (fun _ -> ()) (fun _ -> async { return () })
                     (fun () -> SessionId "t") (fun () -> ChannelId "c") (fun () -> ChatId "ch")
                     args
                 |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | ToolSuccess s -> Assert.Fail($"Expected failure but got: {s}")

[<Fact>]
let ``executeAskUser returns ToolFailure when options are missing`` () =
    let args = parseArgs """{"question":"Choose?"}"""
    let result = executeAskUser (fun _ _ -> ()) (fun _ -> ()) (fun _ -> async { return () })
                     (fun () -> SessionId "t") (fun () -> ChannelId "c") (fun () -> ChatId "ch")
                     args
                 |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | ToolSuccess s -> Assert.Fail($"Expected failure but got: {s}")

[<Fact>]
let ``executeAskUser returns ToolFailure when only one option provided`` () =
    let args = parseArgs """{"question":"Choose?","options":["Only one"]}"""
    let result = executeAskUser (fun _ _ -> ()) (fun _ -> ()) (fun _ -> async { return () })
                     (fun () -> SessionId "t") (fun () -> ChannelId "c") (fun () -> ChatId "ch")
                     args
                 |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | ToolSuccess s -> Assert.Fail($"Expected failure but got: {s}")

[<Fact>]
let ``executeAskUser returns ToolFailure when more than 10 options provided`` () =
    let opts = [ for i in 1..11 -> sprintf "\"Option %d\"" i ] |> String.concat ","
    let args = parseArgs (sprintf """{"question":"Choose?","options":[%s]}""" opts)
    let result = executeAskUser (fun _ _ -> ()) (fun _ -> ()) (fun _ -> async { return () })
                     (fun () -> SessionId "t") (fun () -> ChannelId "c") (fun () -> ChatId "ch")
                     args
                 |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | ToolSuccess s -> Assert.Fail($"Expected failure but got: {s}")

[<Fact>]
let ``executeAskUser returns ToolFailure when options is not an array`` () =
    let args = parseArgs """{"question":"Choose?","options":"Yes"}"""
    let result = executeAskUser (fun _ _ -> ()) (fun _ -> ()) (fun _ -> async { return () })
                     (fun () -> SessionId "t") (fun () -> ChannelId "c") (fun () -> ChatId "ch")
                     args
                 |> Async.RunSynchronously
    match result with
    | ToolFailure _ -> ()
    | ToolSuccess s -> Assert.Fail($"Expected failure but got: {s}")

// ── matchOption (via TCS interaction) ───────────────────────────────────

[<Fact>]
let ``executeAskUser returns ToolSuccess when user selects by exact label`` () =
    let args = parseArgs """{"question":"Pick one?","options":["Apple","Banana","Cherry"]}"""
    let task, storedQuery, registered = runAskUser args

    // Wait for registerPending to be called, then complete TCS
    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQuery.Value.Value.Tcs.SetResult("Banana")

    let result = task.Result
    match result with
    | ToolSuccess msg -> Assert.Contains("Banana", msg)
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")

[<Fact>]
let ``executeAskUser label match is case-insensitive`` () =
    let args = parseArgs """{"question":"Pick one?","options":["Apple","Banana","Cherry"]}"""
    let task, storedQuery, registered = runAskUser args

    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQuery.Value.Value.Tcs.SetResult("apple")   // lowercase

    let result = task.Result
    match result with
    | ToolSuccess msg -> Assert.Contains("Apple", msg)   // returns the canonical form
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")

[<Fact>]
let ``executeAskUser returns ToolSuccess when user selects by 1-based numeric index`` () =
    let args = parseArgs """{"question":"Choose color?","options":["Red","Green","Blue"]}"""
    let task, storedQuery, registered = runAskUser args

    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQuery.Value.Value.Tcs.SetResult("2")   // "Green" is index 2

    let result = task.Result
    match result with
    | ToolSuccess msg -> Assert.Contains("Green", msg)
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")

[<Fact>]
let ``executeAskUser returns ToolSuccess with passthrough when user reply is not a listed option`` () =
    let args = parseArgs """{"question":"Choose?","options":["Yes","No"]}"""
    let task, storedQuery, registered = runAskUser args

    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQuery.Value.Value.Tcs.SetResult("Maybe")   // not in options

    let result = task.Result
    match result with
    | ToolSuccess msg ->
        Assert.Contains("Maybe", msg)
        Assert.Contains("not a listed option", msg)
    | ToolFailure e -> Assert.Fail($"Expected success: {e}")

[<Fact>]
let ``executeAskUser selects first option when index is 1`` () =
    let args = parseArgs """{"question":"Choose?","options":["Alpha","Beta"]}"""
    let task, storedQuery, registered = runAskUser args

    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQuery.Value.Value.Tcs.SetResult("1")

    let result = task.Result
    match result with
    | ToolSuccess msg -> Assert.Contains("Alpha", msg)
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")

[<Fact>]
let ``executeAskUser treats out-of-range numeric index as passthrough`` () =
    let args = parseArgs """{"question":"Choose?","options":["Alpha","Beta"]}"""
    let task, storedQuery, registered = runAskUser args

    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQuery.Value.Value.Tcs.SetResult("99")   // out of range

    let result = task.Result
    match result with
    | ToolSuccess msg -> Assert.Contains("not a listed option", msg)
    | ToolFailure e   -> Assert.Fail($"Expected success: {e}")

[<Fact>]
let ``executeAskUser registers pending query with correct question and options`` () =
    let args = parseArgs """{"question":"What now?","options":["Proceed","Abort","Retry"]}"""
    let task, storedQuery, registered = runAskUser args

    Assert.True(registered.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")

    let q = storedQuery.Value.Value
    Assert.Equal("What now?", q.Question)
    Assert.Equal<string list>([ "Proceed"; "Abort"; "Retry" ], q.Options)

    // Clean up — complete TCS so the task finishes
    q.Tcs.SetResult("Proceed")
    task.Wait(TimeSpan.FromSeconds(2.0)) |> ignore

[<Fact>]
let ``executeAskUser sends outbound message before registering pending query`` () =
    let sent     = ref false
    let registered2 = new ManualResetEventSlim(false)
    let storedQ  = ref Option<PendingUserQuery>.None

    let registerPending _ (q: PendingUserQuery) =
        Assert.True(sent.Value, "send must be called before registerPending")
        storedQ.Value <- Some q
        registered2.Set()

    let send _ = async { sent.Value <- true; return () }
    let args = parseArgs """{"question":"Ok?","options":["OK","Cancel"]}"""
    let task =
        executeAskUser registerPending (fun _ -> ()) send
            (fun () -> SessionId "t") (fun () -> ChannelId "c") (fun () -> ChatId "ch")
            args
        |> Async.StartAsTask

    Assert.True(registered2.Wait(TimeSpan.FromSeconds(2.0)), "registerPending not called")
    storedQ.Value.Value.Tcs.SetResult("OK")
    task.Wait(TimeSpan.FromSeconds(2.0)) |> ignore
