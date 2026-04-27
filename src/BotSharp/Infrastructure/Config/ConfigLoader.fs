module BotSharp.Infrastructure.Config.ConfigLoader

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Config.ConfigParser
open BotSharp.Infrastructure.Shared.AsyncResult

// ═══════════════════════════════════════════════════════════════════════════
// Config file loader
//
// Reads a JSON config file from disk and passes it through ConfigParser.
// Errors are folded into the string channel to keep the API uniform
// (callers only need to handle string errors, not ParseError lists).
// ═══════════════════════════════════════════════════════════════════════════

/// Default config file path: ~/.botsharp/config.json
let defaultConfigPath : string =
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".botsharp",
        "config.json")

/// Load and parse the config at the given path.
/// Returns BotSharpConfig.defaults when the file does not exist (first run).
let loadConfig (path: string) : Async<Result<BotSharpConfig, string>> =
    asyncResult {
        let expanded = expandPath path
        if not (File.Exists expanded) then
            return BotSharpConfig.defaults
        else
            let! text =
                AsyncResult.catch
                    (fun ex -> $"Cannot read config file '{expanded}': {ex.Message}")
                    (File.ReadAllTextAsync expanded |> Async.AwaitTask)
            let! doc =
                try
                    Ok (JsonDocument.Parse text) |> AsyncResult.ofResult
                with ex ->
                    Error $"Invalid JSON in config file '{expanded}': {ex.Message}"
                    |> AsyncResult.ofResult
            use doc = doc
            return!
                parseConfig doc
                |> Result.mapError (fun errs ->
                    errs
                    |> List.map (fun e ->
                        match e with
                        | JsonParseError (msg, pos) -> $"JSON error at {pos}: {msg}"
                        | SchemaError (field, msg)  -> $"Field '{field}': {msg}"
                        | UnknownField name         -> $"Unknown field '{name}'"
                        | MissingField name         -> $"Missing required field '{name}'")
                    |> String.concat "; ")
                |> AsyncResult.ofResult
    }
