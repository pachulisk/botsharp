module BotSharp.Infrastructure.Rules.ClipsNative

open System
open System.Runtime.InteropServices

// ═══════════════════════════════════════════════════════════════════════════
// P/Invoke bindings for CLIPS 6.4 native C library
//
// Only the functions needed by the rule engine are bound here.
// CLIPS API reference: https://www.clipsrules.net/documentation.html
// ═══════════════════════════════════════════════════════════════════════════

[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern nativeint CreateEnvironment()

[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern bool DestroyEnvironment(nativeint env)

/// Load constructs from a file. Returns 0 (LE_NO_ERROR) on success.
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern int Load(nativeint env, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string fileName)

/// Load constructs from a string. Returns true on success.
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern bool LoadFromString(nativeint env, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string theString, unativeint maxPosition)

/// Assert a fact from a string representation. Returns null pointer on failure.
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern nativeint AssertString(nativeint env, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string factString)

/// Run the inference engine. limit = -1 means run until no more rules fire.
/// Returns the number of rules fired.
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern int64 Run(nativeint env, int64 limit)

/// Clear all constructs and facts from the environment.
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern bool Clear(nativeint env)

/// Reset the environment (retract all facts, re-assert initial-fact, reset globals).
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern unit Reset(nativeint env)

/// Evaluate a CLIPS expression. Result is written to the CLIPSValue pointer.
/// Returns 0 (EE_NO_ERROR) on success.
[<DllImport("libclips", CallingConvention = CallingConvention.Cdecl)>]
extern int Eval(nativeint env, [<MarshalAs(UnmanagedType.LPUTF8Str)>] string expression, nativeint result)

// ── CLIPSValue interop ───────────────────────────────────────────────────
// CLIPSValue is a C struct with a union. For simplicity, we allocate a
// sufficiently large buffer and use Eval + a follow-up string extraction.
// The actual struct size varies by platform but 128 bytes is safe.

let internal clipsValueSize = 128

/// Allocate a pinned CLIPSValue buffer for Eval results.
let internal allocClipsValue () : nativeint =
    Marshal.AllocHGlobal(clipsValueSize)

/// Free a CLIPSValue buffer.
let internal freeClipsValue (ptr: nativeint) : unit =
    Marshal.FreeHGlobal(ptr)
