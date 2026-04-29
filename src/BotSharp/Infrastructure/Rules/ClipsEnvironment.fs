module BotSharp.Infrastructure.Rules.ClipsEnvironment

open System
open System.Runtime.InteropServices
open BotSharp.Infrastructure.Rules.ClipsNative

// ═══════════════════════════════════════════════════════════════════════════
// F#-friendly wrapper around the CLIPS native C library
//
// Provides safe lifecycle management (IDisposable), Result-based error
// handling, and string-based fact assertion/query.
//
// CLIPSValue struct layout (CLIPS 6.4, 64-bit):
//   CLIPSValue is a union of pointers (8 bytes total).
//   For INTEGER results: the pointer targets a CLIPSInteger struct where
//     contents (long long) is at offset 32.
//   For STRING results: the pointer targets a CLIPSLexeme struct where
//     contents (const char*) is at offset 32.
// ═══════════════════════════════════════════════════════════════════════════

type ClipsEnv = {
    Handle : nativeint
    mutable Disposed : bool
}

/// Create a new CLIPS environment.
/// Throws if the native library is not found.
let create () : ClipsEnv =
    let h = CreateEnvironment()
    if h = nativeint 0 then
        failwith "CLIPS: CreateEnvironment returned null"
    { Handle = h; Disposed = false }

/// Destroy the CLIPS environment and free native resources.
let destroy (env: ClipsEnv) : unit =
    if not env.Disposed then
        DestroyEnvironment(env.Handle) |> ignore
        env.Disposed <- true

/// Load CLIPS constructs from a string (e.g., deftemplate, defrule).
let loadFromString (env: ClipsEnv) (content: string) : Result<unit, string> =
    if env.Disposed then Error "CLIPS environment disposed"
    else
        let ok = LoadFromString(env.Handle, content, unativeint content.Length)
        if ok then Ok ()
        else Error "CLIPS: LoadFromString failed (syntax error in rules)"

/// Load CLIPS constructs from a file path.
let loadFile (env: ClipsEnv) (filePath: string) : Result<unit, string> =
    if env.Disposed then Error "CLIPS environment disposed"
    else
        let result = Load(env.Handle, filePath)
        if result = 0 then Ok ()   // LE_NO_ERROR = 0
        else Error $"CLIPS: Load failed for {filePath} (error code {result})"

/// Assert a fact from its string representation.
let assertFact (env: ClipsEnv) (factStr: string) : Result<unit, string> =
    if env.Disposed then Error "CLIPS environment disposed"
    else
        let ptr = AssertString(env.Handle, factStr)
        if ptr <> nativeint 0 then Ok ()
        else Error $"CLIPS: AssertString failed for: {factStr}"

/// Run the inference engine. Returns the number of rules fired.
/// limit = -1L means run until agenda is empty.
let run (env: ClipsEnv) (limit: int64) : int64 =
    if env.Disposed then 0L
    else Run(env.Handle, limit)

/// Clear all constructs and facts.
let clear (env: ClipsEnv) : unit =
    if not env.Disposed then Clear(env.Handle) |> ignore

/// Reset the environment (retract all facts, re-assert initial-fact).
let reset (env: ClipsEnv) : unit =
    if not env.Disposed then Reset(env.Handle)

// ── CLIPSValue reading helpers ───────────────────────────────────────────
// CLIPSValue is a union of pointers (8 bytes).
// For Eval results:
//   - The CLIPSValue holds a POINTER to a CLIPSInteger/CLIPSLexeme/etc struct.
//   - CLIPSInteger layout: TypeHeader(2+pad=8) | next(8) | count(8) | bitfields(4+pad=4) | contents(8)
//     → contents (long long) at offset 32
//   - CLIPSLexeme layout: same structure, contents (const char*) at offset 32

/// Offset of `contents` field in CLIPSInteger and CLIPSLexeme structs.
let private contentsOffset = 32

/// Read an integer result from a CLIPSValue buffer after Eval.
let private readIntegerFromClipsValue (buf: nativeint) : int64 option =
    let structPtr = Marshal.ReadIntPtr(buf)   // CLIPSValue is a pointer to the struct
    if structPtr = nativeint 0 then None
    else Some (Marshal.ReadInt64(structPtr, contentsOffset))

/// Read a string result from a CLIPSValue buffer after Eval.
let private readStringFromClipsValue (buf: nativeint) : string option =
    let structPtr = Marshal.ReadIntPtr(buf)   // pointer to CLIPSLexeme
    if structPtr = nativeint 0 then None
    else
        let charPtr = Marshal.ReadIntPtr(structPtr, contentsOffset)  // const char* contents
        if charPtr = nativeint 0 then None
        else Marshal.PtrToStringUTF8(charPtr) |> Option.ofObj

/// Evaluate a CLIPS expression and read the result as an integer.
let evalInt (env: ClipsEnv) (expr: string) : Result<int64, string> =
    if env.Disposed then Error "CLIPS environment disposed"
    else
        let buf = allocClipsValue ()
        try
            let err = Eval(env.Handle, expr, buf)
            if err <> 0 then Error $"CLIPS: Eval failed for: {expr} (error code {err})"
            else
                match readIntegerFromClipsValue buf with
                | Some v -> Ok v
                | None   -> Error "CLIPS: Eval returned null for integer expression"
        finally
            freeClipsValue buf

/// Evaluate a CLIPS expression and read the result as a string.
let evalString (env: ClipsEnv) (expr: string) : Result<string, string> =
    if env.Disposed then Error "CLIPS environment disposed"
    else
        let buf = allocClipsValue ()
        try
            let err = Eval(env.Handle, expr, buf)
            if err <> 0 then Error $"CLIPS: Eval failed for: {expr} (error code {err})"
            else
                match readStringFromClipsValue buf with
                | Some v -> Ok v
                | None   -> Error "CLIPS: Eval returned null for string expression"
        finally
            freeClipsValue buf

/// Query action facts: returns (type, reason, tool) tuples for all action facts.
let queryActionFacts (env: ClipsEnv) : (string * string * string) list =
    if env.Disposed then []
    else
        // Count action facts
        match evalInt env "(length$ (find-all-facts ((?f action)) TRUE))" with
        | Error _ -> []
        | Ok countL ->
            let count = int countL
            if count = 0 then []
            else
                [ for i in 1..count do
                    let readSlot slot =
                        match evalString env $"(fact-slot-value (nth$ {i} (find-all-facts ((?f action)) TRUE)) {slot})" with
                        | Ok v  -> v
                        | Error _ -> ""
                    yield (readSlot "type", readSlot "reason", readSlot "tool") ]
