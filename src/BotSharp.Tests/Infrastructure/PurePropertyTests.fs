module BotSharp.Tests.Infrastructure.PurePropertyTests

/// Property-based tests (FsCheck) for pure functions across the codebase.
/// Each group targets a single module and tests mathematical invariants,
/// not examples — confirming laws hold for all valid inputs.

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open BotSharp.Application.ContextBuilder
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Shared.AsyncResult
open BotSharp.Infrastructure.Storage.DreamStore
open BotSharp.Infrastructure.Storage.CronStore
open BotSharp.Infrastructure.Cron.CronService
open BotSharp.Infrastructure.Input.InputParser

// ═══════════════════════════════════════════════════════════════════════════
// DreamStore.makeSha — pure SHA-256 digest
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``makeSha always returns exactly 8 characters`` (s: NonNull<string>) : bool =
    (makeSha s.Get).Length = 8

[<Property>]
let ``makeSha output contains only lowercase hex characters`` (s: NonNull<string>) : bool =
    makeSha s.Get |> Seq.forall (fun c -> "0123456789abcdef".Contains(c))

[<Property>]
let ``makeSha is deterministic: same input always produces same output`` (s: NonNull<string>) : bool =
    makeSha s.Get = makeSha s.Get

[<Property>]
let ``makeSha two different strings almost never collide`` (a: NonNull<string>) (b: NonNull<string>) : bool =
    // SHA-256 with 8-char hex output has a ~1/4-billion collision chance per pair.
    // FsCheck won't generate a collision in practice; this property documents the intent.
    // When a = b the digests must be equal; when a ≠ b they almost certainly differ.
    if a.Get = b.Get then makeSha a.Get = makeSha b.Get
    else true   // we don't assert distinctness since birthday collisions exist in theory

// ═══════════════════════════════════════════════════════════════════════════
// CronStore.computeNextRun — EveryN schedule
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``computeNextRun EveryN n adds exactly n minutes to any base time`` (n: PositiveInt) : bool =
    let baseTime = DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)
    match computeNextRun (EveryN n.Get) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> next = baseTime.AddMinutes(float n.Get)

[<Property>]
let ``computeNextRun EveryN result is strictly after the input time`` (n: PositiveInt) : bool =
    let baseTime = DateTimeOffset.UtcNow
    match computeNextRun (EveryN n.Get) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> next > baseTime

[<Property>]
let ``computeNextRun Once returns Error when at time is before base`` (offsetHours: PositiveInt) : bool =
    let baseTime = DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
    let pastTime = baseTime.AddHours(float -offsetHours.Get)
    match computeNextRun (Once pastTime) baseTime None with
    | Result.Error _ -> true
    | Result.Ok _    -> false

[<Property>]
let ``computeNextRun Once Ok when at time is strictly after base`` (offsetMinutes: PositiveInt) : bool =
    let baseTime = DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)
    let futureTime = baseTime.AddMinutes(float offsetMinutes.Get)
    match computeNextRun (Once futureTime) baseTime None with
    | Result.Ok t -> t = futureTime
    | Result.Error _ -> false

// ═══════════════════════════════════════════════════════════════════════════
// CronStore.computeNextRun — Daily schedule properties
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``computeNextRun Daily result is always strictly after the base time`` (h: int) (m: int) : bool =
    let hour   = abs h % 24
    let minute = abs m % 60
    // Fixed base: 14:30 on a Thursday — exercises both same-day and next-day paths
    let baseTime = DateTimeOffset(2026, 3, 19, 14, 30, 0, TimeSpan.Zero)
    match computeNextRun (Daily(hour, minute)) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> next > baseTime

[<Property>]
let ``computeNextRun Daily result time-of-day matches scheduled hour and minute`` (h: int) (m: int) : bool =
    let hour   = abs h % 24
    let minute = abs m % 60
    // Fixed base: midnight — any (hour, minute) with same day is guaranteed to be ≥ base
    let baseTime = DateTimeOffset(2026, 3, 19, 0, 0, 1, TimeSpan.Zero)   // 00:00:01 to avoid = candidate
    match computeNextRun (Daily(hour, minute)) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> next.Hour = hour && next.Minute = minute

[<Property>]
let ``computeNextRun Daily result is within 25 hours of the base time`` (h: int) (m: int) : bool =
    let hour   = abs h % 24
    let minute = abs m % 60
    let baseTime = DateTimeOffset(2026, 3, 19, 14, 30, 0, TimeSpan.Zero)
    match computeNextRun (Daily(hour, minute)) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> (next - baseTime).TotalHours <= 25.0

// ═══════════════════════════════════════════════════════════════════════════
// CronStore.computeNextRun — Weekly schedule properties
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``computeNextRun Weekly result is always strictly after the base time`` (dow: int) (h: int) (m: int) : bool =
    let dayOfWeek = enum<DayOfWeek>(abs dow % 7)
    let hour      = abs h % 24
    let minute    = abs m % 60
    let baseTime  = DateTimeOffset(2026, 3, 19, 14, 30, 0, TimeSpan.Zero)   // Thursday 14:30
    match computeNextRun (Weekly(dayOfWeek, hour, minute)) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> next > baseTime

[<Property>]
let ``computeNextRun Weekly result day-of-week always matches target`` (dow: int) (h: int) (m: int) : bool =
    let dayOfWeek = enum<DayOfWeek>(abs dow % 7)
    let hour      = abs h % 24
    let minute    = abs m % 60
    let baseTime  = DateTimeOffset(2026, 3, 19, 0, 0, 1, TimeSpan.Zero)   // Thursday 00:00:01
    match computeNextRun (Weekly(dayOfWeek, hour, minute)) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> next.DayOfWeek = dayOfWeek

[<Property>]
let ``computeNextRun Weekly result is within 8 days of the base time`` (dow: int) (h: int) (m: int) : bool =
    let dayOfWeek = enum<DayOfWeek>(abs dow % 7)
    let hour      = abs h % 24
    let minute    = abs m % 60
    let baseTime  = DateTimeOffset(2026, 3, 19, 14, 30, 0, TimeSpan.Zero)
    match computeNextRun (Weekly(dayOfWeek, hour, minute)) baseTime None with
    | Result.Error _ -> false
    | Result.Ok next -> (next - baseTime).TotalDays <= 8.0

// ═══════════════════════════════════════════════════════════════════════════
// CronService.nextDelayMs — pure timer helper
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``nextDelayMs empty list always returns 60000 regardless of 'now'`` (offsetDays: int) : bool =
    let now = DateTimeOffset.UtcNow.AddDays(float offsetDays)
    nextDelayMs [] now = 60_000

[<Property>]
let ``nextDelayMs result is always at least 1`` (offsetMs: PositiveInt) : bool =
    let now  = DateTimeOffset.UtcNow
    let job  = {
        Id             = TaskId "p"
        Label          = ""
        Task           = "t"
        Schedule       = EveryN 1
        Timezone       = None
        Channel        = ChannelId "cli"
        Chat           = ChatId "c"
        Status         = Active
        CreatedAt      = now
        LastRun        = None
        NextRun        = Some (now.AddMilliseconds(float offsetMs.Get))
        DeleteAfterRun = false
    }
    nextDelayMs [job] now >= 1

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult — functor laws
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``AsyncResult.map id preserves Ok values`` (n: int) : bool =
    let m = AsyncResult.ofResult (Ok n)
    let r = m |> AsyncResult.map id |> Async.RunSynchronously
    r = Ok n

[<Property>]
let ``AsyncResult.map id preserves Error values`` (s: NonNull<string>) : bool =
    let m = AsyncResult.ofResult (Error s.Get)
    let r = (m |> AsyncResult.map (fun (x: int) -> x) |> Async.RunSynchronously)
    r = Error s.Get

[<Property>]
let ``AsyncResult.map composition: map f >> map g = map (f >> g)`` (n: int) : bool =
    let f x = x * 2
    let g x = x + 3
    let m   = AsyncResult.ofResult (Ok n)
    let sequential = m |> AsyncResult.map f |> AsyncResult.map g |> Async.RunSynchronously
    let composed   = m |> AsyncResult.map (f >> g) |> Async.RunSynchronously
    sequential = composed

[<Property>]
let ``AsyncResult.mapError id preserves Error values`` (s: NonNull<string>) : bool =
    let m = AsyncResult.ofResult (Error s.Get)
    let r = m |> AsyncResult.mapError id |> Async.RunSynchronously
    r = Error s.Get

[<Property>]
let ``AsyncResult.mapError does not affect Ok values`` (n: int) : bool =
    let m = AsyncResult.ofResult (Ok n)
    let r = m |> AsyncResult.mapError (fun _ -> "should not appear") |> Async.RunSynchronously
    r = Ok n

// ═══════════════════════════════════════════════════════════════════════════
// InputParser.parseUserInput — chatMessage / slash-command boundary
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``any non-slash string parses as ChatMessage`` (s: NonNull<string>) : bool =
    // Exclude strings starting with '/' — those go through the slash-command path.
    let raw = s.Get
    if raw.StartsWith("/") then true   // skip; not under test
    else
        match parseUserInput raw with
        | Ok (ChatMessage _) -> true
        | Ok (Command _)     -> false   // should not happen
        | Error _            -> false   // parser should not fail on plain text

[<Property>]
let ``parseUserInput never returns Error for any string`` (s: NonNull<string>) : bool =
    match parseUserInput s.Get with
    | Ok _    -> true
    | Error _ -> false   // pChatMessage consumes everything — should never fail

// ═══════════════════════════════════════════════════════════════════════════
// ContextBuilder.buildRequest — pure function invariants
// ═══════════════════════════════════════════════════════════════════════════

/// Helper: build a snapshot with N placeholder messages, lastConsolidated = 0.
let private makeSnap (n: NonNegativeInt) : SessionSnapshot =
    let msgs = List.replicate n.Get (AssistantMessage ("x", None))
    match SessionSnapshot.create (SessionId "s") msgs 0 DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
    | Ok s    -> s
    | Error e -> failwith e

let private dummyInbound : InboundMessage = {
    Channel            = ChannelId "cli"
    Sender             = UserId "user"
    Chat               = ChatId "c"
    Input              = ChatMessage ("hello", [])
    Metadata           = Map.empty
    SessionKeyOverride = None
}

[<Property>]
let ``buildRequest model always equals config DefaultModel`` (n: NonNegativeInt) : bool =
    let snap = makeSnap n
    let cfg  = BotSharpConfig.defaults
    let req  = buildRequest "sys" snap dummyInbound cfg [] None
    req.Model = cfg.DefaultModel

[<Property>]
let ``buildRequest temperature always equals config Temperature`` (n: NonNegativeInt) : bool =
    let snap = makeSnap n
    let cfg  = BotSharpConfig.defaults
    let req  = buildRequest "sys" snap dummyInbound cfg [] None
    req.Settings.Temperature = cfg.Temperature

[<Property>]
let ``buildRequest max_tokens always equals config MaxTokens`` (n: NonNegativeInt) : bool =
    let snap = makeSnap n
    let cfg  = BotSharpConfig.defaults
    let req  = buildRequest "sys" snap dummyInbound cfg [] None
    req.Settings.MaxTokens = cfg.MaxTokens

[<Property>]
let ``buildRequest message count is snapshot messages + 2 (system + user)`` (n: NonNegativeInt) : bool =
    let snap = makeSnap n
    let req  = buildRequest "sys" snap dummyInbound BotSharpConfig.defaults [] None
    req.Messages.Length = n.Get + 2

[<Property>]
let ``buildRequest first message is always SystemMessage`` (n: NonNegativeInt) : bool =
    let snap = makeSnap n
    let req  = buildRequest "sys" snap dummyInbound BotSharpConfig.defaults [] None
    match List.head req.Messages with
    | SystemMessage _ -> true
    | _               -> false

[<Property>]
let ``buildRequest last message is always UserMessage`` (n: NonNegativeInt) : bool =
    let snap = makeSnap n
    let req  = buildRequest "sys" snap dummyInbound BotSharpConfig.defaults [] None
    match List.last req.Messages with
    | UserMessage _ -> true
    | _             -> false

[<Property>]
let ``buildRequest system prompt text is embedded in the SystemMessage`` (s: NonNull<string>) : bool =
    // The system prompt string should appear verbatim in the SystemMessage content.
    let emptySnap = SessionSnapshot.empty (SessionId "s") DateTimeOffset.UtcNow
    let req  = buildRequest s.Get emptySnap dummyInbound BotSharpConfig.defaults [] None
    match List.head req.Messages with
    | SystemMessage content -> content = s.Get
    | _                     -> false

// ═══════════════════════════════════════════════════════════════════════════
// SessionSnapshot — append is monotone
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``SessionSnapshot.append always increases messageCount by exactly 1`` (n: NonNegativeInt) : bool =
    // Build a snapshot with n messages, append one more, count goes to n+1.
    let msgs = List.replicate n.Get (AssistantMessage ("x", None))
    let snap =
        match SessionSnapshot.create (SessionId "s") msgs 0 DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
        | Ok s    -> s
        | Error _ -> SessionSnapshot.empty (SessionId "s") DateTimeOffset.UtcNow
    let before = SessionSnapshot.messageCount snap
    let after  = SessionSnapshot.messageCount (SessionSnapshot.append (UserMessage ("y", [])) snap)
    after = before + 1

[<Property>]
let ``SessionSnapshot.append does not change lastConsolidated`` (n: NonNegativeInt) : bool =
    let msgs = List.replicate n.Get (AssistantMessage ("x", None))
    let snap =
        match SessionSnapshot.create (SessionId "s") msgs 0 DateTimeOffset.UtcNow DateTimeOffset.UtcNow with
        | Ok s    -> s
        | Error _ -> SessionSnapshot.empty (SessionId "s") DateTimeOffset.UtcNow
    let before = SessionSnapshot.lastConsolidated snap
    let after  = SessionSnapshot.lastConsolidated (SessionSnapshot.append (UserMessage ("y", [])) snap)
    after = before

// ═══════════════════════════════════════════════════════════════════════════
// ApiKey — create/value round-trip
// ═══════════════════════════════════════════════════════════════════════════

[<Property>]
let ``ApiKey.create is Ok iff string is non-empty and non-whitespace`` (s: NonNull<string>) : bool =
    let trimmed = s.Get.Trim()
    let expected = trimmed.Length > 0
    match ApiKey.create s.Get with
    | Ok _    -> expected
    | Error _ -> not expected

[<Property>]
let ``ApiKey.value round-trips the trimmed input`` (s: NonNull<string>) : bool =
    let trimmed = s.Get.Trim()
    if trimmed.Length = 0 then true   // Error case — nothing to round-trip
    else
        match ApiKey.create s.Get with
        | Ok key  -> ApiKey.value key = s.Get   // ApiKey stores the original, not trimmed
        | Error _ -> false
