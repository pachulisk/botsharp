module BotSharp.Tests.Infrastructure.EventBusTests

open System
open System.IO
open System.Threading
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.EventBus.EventBus
open BotSharp.Infrastructure.EventBus.SqliteLogger
open BotSharp.Infrastructure.Storage.StateDb

// ═══════════════════════════════════════════════════════════════════════════
// EventBus and SqliteLogger unit tests
//
// mkEvent / formatEvents are pure — tested directly.
// EventBus.create is mailbox-based — tested via publish/subscribe.
// createConsumer / queryEvents are tested against real file-based SQLite.
// ═══════════════════════════════════════════════════════════════════════════

/// Create a real file-based StateDb.
let private mkDb () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let factory = init tmp |> Async.RunSynchronously
    (factory, tmp)

// ── mkEvent ───────────────────────────────────────────────────────────────

[<Fact>]
let ``mkEvent sets category and kind`` () =
    let evt = mkEvent "llm" "llm.call.start" None []
    Assert.Equal("llm", evt.Category)
    Assert.Equal("llm.call.start", evt.Kind)

[<Fact>]
let ``mkEvent sets sessionId when provided`` () =
    let evt = mkEvent "session" "session.start" (Some "cli:test-session") []
    Assert.Equal(Some "cli:test-session", evt.SessionId)

[<Fact>]
let ``mkEvent leaves sessionId None when not provided`` () =
    let evt = mkEvent "system" "system.boot" None []
    Assert.True(evt.SessionId.IsNone)

[<Fact>]
let ``mkEvent populates Data map from list`` () =
    let evt = mkEvent "tool" "tool.exec.end" None [ "tool", "bash"; "exit_code", "0" ]
    Assert.Equal("bash", evt.Data |> Map.find "tool")
    Assert.Equal("0", evt.Data |> Map.find "exit_code")

[<Fact>]
let ``mkEvent generates non-empty Id`` () =
    let evt = mkEvent "system" "system.ping" None []
    Assert.False(String.IsNullOrEmpty(evt.Id))

[<Fact>]
let ``mkEvent generates unique Ids on each call`` () =
    let e1 = mkEvent "system" "ping" None []
    let e2 = mkEvent "system" "ping" None []
    Assert.NotEqual<string>(e1.Id, e2.Id)

[<Fact>]
let ``mkEvent Timestamp is close to now`` () =
    let before = DateTimeOffset.UtcNow.AddSeconds(-1.0)
    let evt = mkEvent "system" "ping" None []
    let after = DateTimeOffset.UtcNow.AddSeconds(1.0)
    Assert.True(evt.Timestamp >= before && evt.Timestamp <= after)

// ── formatEvents ──────────────────────────────────────────────────────────

[<Fact>]
let ``formatEvents returns placeholder for empty list`` () =
    let result = formatEvents []
    Assert.Equal("(no events)", result)

[<Fact>]
let ``formatEvents includes event kind in output`` () =
    let evt = { mkEvent "llm" "llm.call.start" None [] with Timestamp = DateTimeOffset.UtcNow }
    let result = formatEvents [ evt ]
    Assert.Contains("llm.call.start", result)

[<Fact>]
let ``formatEvents includes session id snippet in output`` () =
    // "cli:test" is 8 chars (≤12), so it is not truncated
    let evt = { mkEvent "session" "session.start" (Some "cli:test") [] with Timestamp = DateTimeOffset.UtcNow }
    let result = formatEvents [ evt ]
    Assert.Contains("cli:test", result)

[<Fact>]
let ``formatEvents truncates session id longer than 12 chars`` () =
    let longSid = "cli:averylongsessionid12345"
    let evt = { mkEvent "session" "session.start" (Some longSid) [] with Timestamp = DateTimeOffset.UtcNow }
    let result = formatEvents [ evt ]
    // Truncated to first 12 chars: "cli:averylon"
    Assert.Contains("cli:averylon", result)
    Assert.DoesNotContain(longSid, result)

[<Fact>]
let ``formatEvents shows data key-value pairs`` () =
    let evt = { mkEvent "tool" "tool.exec.end" None [ "tool", "bash"; "exit_code", "0" ] with Timestamp = DateTimeOffset.UtcNow }
    let result = formatEvents [ evt ]
    Assert.Contains("tool=bash", result)
    Assert.Contains("exit_code=0", result)

[<Fact>]
let ``formatEvents includes event count in header`` () =
    let evts = [ mkEvent "system" "a" None []; mkEvent "system" "b" None []; mkEvent "system" "c" None [] ]
    let result = formatEvents evts
    Assert.Contains("3", result)

// ── EventBus publish/subscribe ────────────────────────────────────────────

[<Fact>]
let ``EventBus delivers published event to subscriber on log channel`` () =
    let bus = create ()
    let received = new ManualResetEventSlim(false)
    let mutable capturedKind = ""
    bus.Subscribe "log" (fun evt -> async {
        capturedKind <- evt.Kind
        received.Set()
    }) |> ignore
    bus.Publish(mkEvent "system" "test.event.kind" None [])
    Assert.True(received.Wait(TimeSpan.FromSeconds(2.0)), "subscriber not called")
    Assert.Equal("test.event.kind", capturedKind)

[<Fact>]
let ``EventBus delivers to multiple subscribers on same channel`` () =
    let bus = create ()
    let count = ref 0
    let latch = new CountdownEvent(2)
    bus.Subscribe "log" (fun _ -> async { Interlocked.Increment(count) |> ignore; latch.Signal() |> ignore }) |> ignore
    bus.Subscribe "log" (fun _ -> async { Interlocked.Increment(count) |> ignore; latch.Signal() |> ignore }) |> ignore
    bus.Publish(mkEvent "system" "multi" None [])
    Assert.True(latch.Wait(TimeSpan.FromSeconds(2.0)), "not all subscribers called")
    Assert.Equal(2, count.Value)

[<Fact>]
let ``EventBus routes event to custom channel via AddRouter`` () =
    let bus = create ()
    let received = new ManualResetEventSlim(false)
    let mutable capturedCategory = ""
    bus.AddChannel "tools"
    bus.AddRouter { Match = (fun evt -> evt.Category = "tool"); Channel = "tools" }
    bus.Subscribe "tools" (fun evt -> async {
        capturedCategory <- evt.Category
        received.Set()
    }) |> ignore
    bus.Publish(mkEvent "tool" "tool.exec.end" None [])
    Assert.True(received.Wait(TimeSpan.FromSeconds(2.0)), "custom channel subscriber not called")
    Assert.Equal("tool", capturedCategory)

[<Fact>]
let ``EventBus log channel receives all events regardless of routers`` () =
    let bus = create ()
    let received = new ManualResetEventSlim(false)
    bus.AddRouter { Match = (fun _ -> false); Channel = "nowhere" }
    bus.Subscribe "log" (fun _ -> async { received.Set() }) |> ignore
    bus.Publish(mkEvent "system" "fallback" None [])
    Assert.True(received.Wait(TimeSpan.FromSeconds(2.0)), "log channel did not receive event")

// ── createConsumer + queryEvents ──────────────────────────────────────────

[<Fact>]
let ``createConsumer writes event to event_log and queryEvents returns it`` () =
    let openDb, tmp = mkDb ()
    try
        let consumer = createConsumer openDb
        let evt = mkEvent "llm" "llm.call.start" (Some "cli:test") [ "model", "gpt-4o" ]
        consumer evt |> Async.RunSynchronously
        use conn = openDb ()
        let events = queryEvents conn None None 10
        Assert.Equal(1, events.Length)
        let e = events.[0]
        Assert.Equal("llm", e.Category)
        Assert.Equal("llm.call.start", e.Kind)
        Assert.Equal(Some "cli:test", e.SessionId)
        Assert.Equal("gpt-4o", e.Data |> Map.find "model")
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``queryEvents filters by category`` () =
    let openDb, tmp = mkDb ()
    try
        let consumer = createConsumer openDb
        consumer (mkEvent "llm"  "llm.call"   None []) |> Async.RunSynchronously
        consumer (mkEvent "tool" "tool.exec"  None []) |> Async.RunSynchronously
        consumer (mkEvent "llm"  "llm.call.2" None []) |> Async.RunSynchronously
        use conn = openDb ()
        let llmEvents  = queryEvents conn (Some "llm")  None 10
        let toolEvents = queryEvents conn (Some "tool") None 10
        Assert.Equal(2, llmEvents.Length)
        Assert.Equal(1, toolEvents.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``queryEvents filters by session`` () =
    let openDb, tmp = mkDb ()
    try
        let consumer = createConsumer openDb
        consumer (mkEvent "session" "start" (Some "cli:s1") []) |> Async.RunSynchronously
        consumer (mkEvent "session" "start" (Some "cli:s2") []) |> Async.RunSynchronously
        use conn = openDb ()
        let s1Events = queryEvents conn None (Some "cli:s1") 10
        Assert.Equal(1, s1Events.Length)
        Assert.Equal(Some "cli:s1", s1Events.[0].SessionId)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``queryEvents respects limit`` () =
    let openDb, tmp = mkDb ()
    try
        let consumer = createConsumer openDb
        for i in 1..5 do
            consumer (mkEvent "system" (sprintf "ping.%d" i) None []) |> Async.RunSynchronously
        use conn = openDb ()
        let events = queryEvents conn None None 3
        Assert.Equal(3, events.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``queryEvents returns empty list when no events match`` () =
    let openDb, tmp = mkDb ()
    try
        use conn = openDb ()
        let events = queryEvents conn (Some "nonexistent-category") None 10
        Assert.Empty(events)
    finally
        try Directory.Delete(tmp, true) with _ -> ()
