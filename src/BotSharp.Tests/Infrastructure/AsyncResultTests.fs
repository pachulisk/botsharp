module BotSharp.Tests.Infrastructure.AsyncResultTests

open System
open Xunit
open BotSharp.Infrastructure.Shared.AsyncResult

// ─── shorthand for running AsyncResult ───────────────────────────────────────
let private run (m: AsyncResult<'a, 'e>) = m |> Async.RunSynchronously

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.ofResult
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ofResult Ok lifts Ok into AsyncResult`` () =
    match run (AsyncResult.ofResult (Ok 42)) with
    | Ok v  -> Assert.Equal(42, v)
    | Error e -> Assert.Fail($"Expected Ok 42, got Error {e}")

[<Fact>]
let ``ofResult Error lifts Error into AsyncResult`` () =
    match run (AsyncResult.ofResult (Error "boom")) with
    | Error e -> Assert.Equal("boom", e)
    | Ok v    -> Assert.Fail($"Expected Error, got Ok {v}")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.ofAsync
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ofAsync wraps a plain Async value as Ok`` () =
    let m = AsyncResult.ofAsync (async { return 99 })
    match run m with
    | Ok v  -> Assert.Equal(99, v)
    | Error e -> Assert.Fail($"Expected Ok 99, got Error {e}")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.map
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``map transforms Ok value`` () =
    let m = AsyncResult.ofResult (Ok 5) |> AsyncResult.map (fun x -> x * 2)
    match run m with
    | Ok v    -> Assert.Equal(10, v)
    | Error e -> Assert.Fail($"Expected Ok 10, got Error {e}")

[<Fact>]
let ``map does not execute on Error`` () =
    let mutable called = false
    let m =
        AsyncResult.ofResult (Error "err")
        |> AsyncResult.map (fun _ -> called <- true; 42)
    let _ = run m
    Assert.False(called, "map function should not be called on Error track")

[<Fact>]
let ``map propagates Error unchanged`` () =
    let m = AsyncResult.ofResult (Error "original") |> AsyncResult.map (fun x -> x + 1)
    match run m with
    | Error e -> Assert.Equal("original", e)
    | Ok v    -> Assert.Fail($"Expected Error, got Ok {v}")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.mapError
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``mapError transforms Error value`` () =
    let m =
        AsyncResult.ofResult (Error 42)
        |> AsyncResult.mapError (fun e -> $"error code {e}")
    match run m with
    | Error e -> Assert.Equal("error code 42", e)
    | Ok v    -> Assert.Fail($"Expected Error, got Ok {v}")

[<Fact>]
let ``mapError does not execute on Ok`` () =
    let mutable called = false
    let m =
        AsyncResult.ofResult (Ok "value")
        |> AsyncResult.mapError (fun _ -> called <- true; "new error")
    let _ = run m
    Assert.False(called, "mapError should not be called on Ok track")

[<Fact>]
let ``mapError propagates Ok unchanged`` () =
    let m =
        AsyncResult.ofResult (Ok "hello")
        |> AsyncResult.mapError (fun _ -> "should not appear")
    match run m with
    | Ok v    -> Assert.Equal("hello", v)
    | Error e -> Assert.Fail($"Expected Ok, got Error {e}")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.ignore
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``ignore converts Ok value to Ok unit`` () =
    let m = AsyncResult.ofResult (Ok 42) |> AsyncResult.ignore
    match run m with
    | Ok ()   -> ()
    | Error e -> Assert.Fail($"Expected Ok (), got Error {e}")

[<Fact>]
let ``ignore propagates Error unchanged`` () =
    let m = AsyncResult.ofResult (Error "fail") |> AsyncResult.ignore
    match run m with
    | Error e -> Assert.Equal("fail", e)
    | Ok ()   -> Assert.Fail("Expected Error, got Ok ()")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.catch
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``catch returns Ok when async succeeds`` () =
    let m = AsyncResult.catch (fun ex -> ex.Message) (async { return 7 })
    match run m with
    | Ok v    -> Assert.Equal(7, v)
    | Error e -> Assert.Fail($"Expected Ok 7, got Error {e}")

[<Fact>]
let ``catch returns Error when async throws`` () =
    let m =
        AsyncResult.catch
            (fun ex -> $"caught: {ex.Message}")
            (async { raise (InvalidOperationException("test error")); return 0 })
    match run m with
    | Error e -> Assert.Contains("caught:", e); Assert.Contains("test error", e)
    | Ok v    -> Assert.Fail($"Expected Error, got Ok {v}")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.sequence — sequential, short-circuits on Error
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``sequence of empty list returns Ok empty list`` () =
    let m = AsyncResult.sequence []
    match run m with
    | Ok []   -> ()
    | other   -> Assert.Fail($"Expected Ok [], got {other}")

[<Fact>]
let ``sequence of all-Ok list returns Ok with all values`` () =
    let ms = [ AsyncResult.ofResult (Ok 1); AsyncResult.ofResult (Ok 2); AsyncResult.ofResult (Ok 3) ]
    match run (AsyncResult.sequence ms) with
    | Ok [1; 2; 3] -> ()
    | other -> Assert.Fail($"Expected Ok [1;2;3], got {other}")

[<Fact>]
let ``sequence short-circuits on first Error`` () =
    let mutable thirdCalled = false
    let ms = [
        AsyncResult.ofResult (Ok 1)
        AsyncResult.ofResult (Error "stop here")
        async { thirdCalled <- true; return Ok 3 }
    ]
    let result = run (AsyncResult.sequence ms)
    match result with
    | Error "stop here" ->
        Assert.False(thirdCalled, "Third element should not be executed after Error")
    | other -> Assert.Fail($"Expected Error 'stop here', got {other}")

[<Fact>]
let ``sequence preserves order of Ok values`` () =
    let ms = [ 10; 20; 30 ] |> List.map (fun v -> AsyncResult.ofResult (Ok v))
    match run (AsyncResult.sequence ms) with
    | Ok vs -> Assert.Equal<int list>([10; 20; 30], vs)
    | Error e -> Assert.Fail($"Expected Ok [10;20;30], got Error {e}")

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult.sequenceParallel — parallel, collects Ok or returns first Error
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``sequenceParallel of empty list returns Ok empty list`` () =
    match run (AsyncResult.sequenceParallel []) with
    | Ok []  -> ()
    | other  -> Assert.Fail($"Expected Ok [], got {other}")

[<Fact>]
let ``sequenceParallel of all-Ok list returns Ok with all values`` () =
    let ms = [ AsyncResult.ofResult (Ok 10); AsyncResult.ofResult (Ok 20) ]
    match run (AsyncResult.sequenceParallel ms) with
    | Ok vs ->
        Assert.Equal(2, vs.Length)
        Assert.Contains(10, vs)
        Assert.Contains(20, vs)
    | Error e -> Assert.Fail($"Expected Ok list, got Error {e}")

[<Fact>]
let ``sequenceParallel returns Error when any element fails`` () =
    let ms = [
        AsyncResult.ofResult (Ok 1)
        AsyncResult.ofResult (Error "parallel fail")
        AsyncResult.ofResult (Ok 3)
    ]
    match run (AsyncResult.sequenceParallel ms) with
    | Error e -> Assert.Equal("parallel fail", e)
    | Ok vs   -> Assert.Fail($"Expected Error, got Ok {vs}")

// ═══════════════════════════════════════════════════════════════════════════
// asyncResult computation expression
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``asyncResult CE lets Bind chain Ok values`` () =
    let m = asyncResult {
        let! x = AsyncResult.ofResult (Ok 3)
        let! y = AsyncResult.ofResult (Ok 4)
        return x + y
    }
    match run m with
    | Ok 7    -> ()
    | other   -> Assert.Fail($"Expected Ok 7, got {other}")

[<Fact>]
let ``asyncResult CE short-circuits on Error`` () =
    let mutable secondCalled = false
    let m = asyncResult {
        let! _ = AsyncResult.ofResult (Error "stop")
        secondCalled <- true
        return 99
    }
    let _ = run m
    Assert.False(secondCalled, "CE should not continue after Error")

[<Fact>]
let ``asyncResult CE returns Error from first failing step`` () =
    let m = asyncResult {
        let! x = AsyncResult.ofResult (Ok 1)
        let! _ = AsyncResult.ofResult (Error "mid-error")
        return x + 10
    }
    match run m with
    | Error "mid-error" -> ()
    | other -> Assert.Fail($"Expected Error 'mid-error', got {other}")

[<Fact>]
let ``asyncResult CE return wraps value in Ok`` () =
    let m = asyncResult { return 42 }
    match run m with
    | Ok 42   -> ()
    | other   -> Assert.Fail($"Expected Ok 42, got {other}")

// ─────────────────────────────────────────────────────────────────────────────
// Zero — called when if-without-else evaluates the false branch
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``asyncResult CE Zero is Ok unit when if-without-else condition is false`` () =
    let m : AsyncResult<unit, string> = asyncResult {
        if false then
            do! AsyncResult.ofResult (Ok ())
    }
    match run m with
    | Ok ()   -> ()
    | Error e -> Assert.Fail($"Expected Ok (), got Error {e}")

// ─────────────────────────────────────────────────────────────────────────────
// Combine — sequences a unit-returning do! with the subsequent step
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``asyncResult CE Combine sequences unit step then next step`` () =
    let mutable sideEffect = false
    let m = asyncResult {
        do! AsyncResult.ofResult (Ok ())
        sideEffect <- true
        return 42
    }
    match run m with
    | Ok 42   -> Assert.True(sideEffect, "Side effect should have run after do!")
    | other   -> Assert.Fail($"Expected Ok 42, got {other}")

[<Fact>]
let ``asyncResult CE Combine short-circuits when unit step is Error`` () =
    let mutable secondRan = false
    let m : AsyncResult<int, string> = asyncResult {
        do! AsyncResult.ofResult (Error "combine-stop")
        secondRan <- true
        return 99
    }
    let _ = run m
    Assert.False(secondRan, "Second step must not run when first do! is Error")

// ─────────────────────────────────────────────────────────────────────────────
// TryWith — catches exceptions thrown inside the CE body
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``asyncResult CE TryWith catches exception thrown inside computation`` () =
    let m = asyncResult {
        try
            raise (InvalidOperationException "oops")
            return "no"
        with ex ->
            return $"caught: {ex.Message}"
    }
    match run m with
    | Ok msg  -> Assert.Contains("caught: oops", msg)
    | Error e -> Assert.Fail($"Expected Ok with caught message, got Error {e}")

[<Fact>]
let ``asyncResult CE TryWith propagates Ok when no exception is raised`` () =
    let m = asyncResult {
        try
            return 5
        with _ ->
            return 0
    }
    match run m with
    | Ok 5    -> ()
    | other   -> Assert.Fail($"Expected Ok 5, got {other}")

// ─────────────────────────────────────────────────────────────────────────────
// TryFinally — compensation runs on both Ok and Error paths
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``asyncResult CE TryFinally compensation runs on Ok path`` () =
    let mutable ran = false
    let m = asyncResult {
        try
            return 1
        finally
            ran <- true
    }
    let _ = run m
    Assert.True(ran, "Compensation must run on success path")

[<Fact>]
let ``asyncResult CE TryFinally compensation runs on Error path`` () =
    let mutable ran = false
    let m : AsyncResult<int, string> = asyncResult {
        try
            return! AsyncResult.ofResult (Error "err")
        finally
            ran <- true
    }
    let _ = run m
    Assert.True(ran, "Compensation must run even when result is Error")

// ─────────────────────────────────────────────────────────────────────────────
// Using — disposes the resource after computation (Ok or Error)
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``asyncResult CE Using disposes resource after Ok`` () =
    let mutable disposed = false
    let resource = { new System.IDisposable with member _.Dispose() = disposed <- true }
    let m = asyncResult {
        use _ = resource
        return 42
    }
    let _ = run m
    Assert.True(disposed, "Resource should be disposed after successful computation")

[<Fact>]
let ``asyncResult CE Using disposes resource after Error`` () =
    let mutable disposed = false
    let resource = { new System.IDisposable with member _.Dispose() = disposed <- true }
    let m : AsyncResult<int, string> = asyncResult {
        use _ = resource
        return! AsyncResult.ofResult (Error "err")
    }
    let _ = run m
    Assert.True(disposed, "Resource should be disposed even when computation returns Error")
