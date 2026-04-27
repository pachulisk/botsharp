module BotSharp.Infrastructure.Shared.AsyncResult

// ═══════════════════════════════════════════════════════════════════════════
// AsyncResult computation expression
//
// Wraps Async<Result<'a, 'e>> so that railway-oriented pipelines read like
// sequential imperative code.  On the Error track the CE short-circuits
// without executing subsequent steps — no try/catch needed.
// ═══════════════════════════════════════════════════════════════════════════

type AsyncResult<'a, 'e> = Async<Result<'a, 'e>>

type AsyncResultBuilder() =
    member _.Return(x: 'a) : AsyncResult<'a, 'e> =
        async { return Ok x }

    member _.ReturnFrom(m: AsyncResult<'a, 'e>) : AsyncResult<'a, 'e> = m

    member _.Zero() : AsyncResult<unit, 'e> =
        async { return Ok () }

    member _.Bind(m: AsyncResult<'a, 'e>, f: 'a -> AsyncResult<'b, 'e>) : AsyncResult<'b, 'e> =
        async {
            let! result = m
            match result with
            | Ok x    -> return! f x
            | Error e -> return Error e
        }

    member _.Combine(a: AsyncResult<unit, 'e>, b: AsyncResult<'b, 'e>) : AsyncResult<'b, 'e> =
        async {
            let! result = a
            match result with
            | Ok ()   -> return! b
            | Error e -> return Error e
        }

    member _.Delay(f: unit -> AsyncResult<'a, 'e>) : AsyncResult<'a, 'e> = async { return! f () }

    member _.TryWith(m: AsyncResult<'a, 'e>, handler: exn -> AsyncResult<'a, 'e>) : AsyncResult<'a, 'e> =
        async {
            try
                return! m
            with ex ->
                return! handler ex
        }

    member _.TryFinally(m: AsyncResult<'a, 'e>, compensation: unit -> unit) : AsyncResult<'a, 'e> =
        async {
            try
                return! m
            finally
                compensation ()
        }

    member _.Using(resource: 'r :> System.IDisposable, f: 'r -> AsyncResult<'a, 'e>) : AsyncResult<'a, 'e> =
        async {
            use r = resource
            return! f r
        }

let asyncResult = AsyncResultBuilder()

// ═══════════════════════════════════════════════════════════════════════════
// Utility functions for AsyncResult
// ═══════════════════════════════════════════════════════════════════════════

module AsyncResult =

    /// Lift a Result into AsyncResult
    let ofResult (r: Result<'a, 'e>) : AsyncResult<'a, 'e> =
        async { return r }

    /// Lift a plain Async into AsyncResult (errors modelled as Result<_, _>)
    let ofAsync (m: Async<'a>) : AsyncResult<'a, 'e> =
        async {
            let! v = m
            return Ok v
        }

    /// Transform the error channel
    let mapError (f: 'e1 -> 'e2) (m: AsyncResult<'a, 'e1>) : AsyncResult<'a, 'e2> =
        async {
            let! r = m
            return Result.mapError f r
        }

    /// Transform the success channel
    let map (f: 'a -> 'b) (m: AsyncResult<'a, 'e>) : AsyncResult<'b, 'e> =
        async {
            let! r = m
            return Result.map f r
        }

    /// Ignore the success value (returns unit on success)
    let ignore (m: AsyncResult<'a, 'e>) : AsyncResult<unit, 'e> =
        map (fun _ -> ()) m

    /// Run a list of AsyncResult in parallel; collect all Ok or return first Error
    let sequenceParallel (xs: AsyncResult<'a, 'e> list) : AsyncResult<'a list, 'e> =
        async {
            let! results = xs |> List.map id |> Async.Parallel
            let oks = results |> Array.choose (function Ok x -> Some x | Error _ -> None)
            let err = results |> Array.tryPick (function Error e -> Some e | Ok _ -> None)
            return
                match err with
                | Some e -> Error e
                | None   -> Ok (Array.toList oks)
        }

    /// Run a list of AsyncResult sequentially; short-circuit on first Error
    let sequence (xs: AsyncResult<'a, 'e> list) : AsyncResult<'a list, 'e> =
        async {
            let mutable acc = []
            let mutable error = None
            for m in xs do
                if error.IsNone then
                    let! r = m
                    match r with
                    | Ok v    -> acc <- acc @ [v]
                    | Error e -> error <- Some e
            return
                match error with
                | Some e -> Error e
                | None   -> Ok acc
        }

    /// Wrap an Async that might throw into AsyncResult
    let catch (errMap: exn -> 'e) (m: Async<'a>) : AsyncResult<'a, 'e> =
        async {
            try
                let! v = m
                return Ok v
            with ex ->
                return Error (errMap ex)
        }
