module BotSharp.Infrastructure.Channels.OnboardingWizard

open System
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Config.ConfigWriter

// ═══════════════════════════════════════════════════════════════════════════
// Type-safe CLI onboarding wizard
//
// Design principles:
//
// 1. PromptResult<'T> — makes Back/Quit/Skip structurally distinct from a
//    valid value.  There is no way to accidentally treat a navigation intent
//    as a Value: the DU cases are disjoint.
//
// 2. ProviderChoice DU — after the user's input passes parseProviderChoice,
//    the choice is typed.  Invalid provider names are unrepresentable inside
//    the wizard.  The conversion to string happens exactly once at `finish`.
//
// 3. Per-step parsers (string → Result<'T, string>) — each step has a
//    dedicated parser.  `prompt` retries until the parser succeeds or the
//    user types "back"/"quit".  Domain functions never see unvalidated input.
//
// 4. Mutually recursive step functions — `Back` re-invokes the previous
//    step function with the current state; no index arithmetic, no mutable
//    step counter.
// ═══════════════════════════════════════════════════════════════════════════

// ── Navigation result ────────────────────────────────────────────────────────

/// Outcome of a single wizard prompt.
/// Value/Skip carry validated data; Back/Quit are navigation intents.
/// Using a DU makes it structurally impossible to treat Back or Quit as a value.
type PromptResult<'T> =
    | Value of 'T   // parser succeeded; proceed with this value
    | Skip          // user pressed Enter — accept the current default
    | Back          // user typed "back" — re-enter the previous step
    | Quit          // user typed "quit" — abort the wizard

/// Read one line, interpret meta-commands, run the parser.
/// Retries on parse failure; never returns a Value that the parser rejected.
let private prompt (label: string) (parser: string -> Result<'T, string>) : PromptResult<'T> =
    let rec ask () =
        printf "  %s: " label
        Console.Out.Flush()
        match Console.ReadLine() with
        | null   -> Quit
        | "quit" -> Quit
        | "back" -> Back
        | ""     -> Skip
        | raw    ->
            match parser raw with
            | Result.Ok v    -> Value v
            | Result.Error m -> printfn "  ✗ %s" m; ask ()
    ask ()

// ── ProviderChoice DU ────────────────────────────────────────────────────────

/// Makes invalid provider names unrepresentable after the parse boundary.
/// Converted to string ONCE at the `finish` boundary via `providerChoiceName`.
type private ProviderChoice = OpenAI | Anthropic | Gemini | DeepSeek | Ollama

let private providerChoiceName = function
    | OpenAI    -> "openai"
    | Anthropic -> "anthropic"
    | Gemini    -> "gemini"
    | DeepSeek  -> "deepseek"
    | Ollama    -> "ollama"

let private providerDefaultModel = function
    | OpenAI    -> "gpt-4o-mini"
    | Anthropic -> "claude-sonnet-4-5-20251001"
    | Gemini    -> "gemini-2.0-flash"
    | DeepSeek  -> "deepseek-v4-pro"
    | Ollama    -> "llama3"

// ── Per-step parsers ─────────────────────────────────────────────────────────

/// Returns a ProviderChoice DU — not a string.
/// Illegal provider names are structurally unrepresentable past this call.
let private parseProviderChoice (raw: string) : Result<ProviderChoice, string> =
    match raw.Trim().ToLowerInvariant() with
    | "1" | "openai"    -> Result.Ok OpenAI
    | "2" | "anthropic" -> Result.Ok Anthropic
    | "3" | "gemini"    -> Result.Ok Gemini
    | "4" | "deepseek"  -> Result.Ok DeepSeek
    | "5" | "ollama"    -> Result.Ok Ollama
    | other             -> Result.Error $"Unknown provider '{other}'. Enter 1–5 or a provider name."

let private parseApiKey (raw: string) : Result<ApiKey, string> =
    ApiKey.create (raw.Trim())

let private parseTemperature (raw: string) : Result<float, string> =
    match Double.TryParse(raw.Trim(),
                          Globalization.NumberStyles.Float,
                          Globalization.CultureInfo.InvariantCulture) with
    | true, v when v >= 0.0 && v <= 2.0 -> Result.Ok v
    | true, _  -> Result.Error "Temperature must be between 0.0 and 2.0"
    | false, _ -> Result.Error "Enter a decimal number (e.g. 0.7)"

let private parseMaxTokens (raw: string) : Result<int, string> =
    match Int32.TryParse(raw.Trim()) with
    | true, v when v > 0 -> Result.Ok v
    | true, _  -> Result.Error "Max tokens must be a positive integer"
    | false, _ -> Result.Error "Enter a positive integer (e.g. 4096)"

let private parseModel (raw: string) : Result<string, string> =
    let s = raw.Trim()
    if s.Length > 0 then Result.Ok s
    else Result.Error "Model name cannot be empty"

/// Rejects empty strings and expands ~ to $HOME.
/// The path is the single boundary where raw user input becomes a typed path.
let private parseWorkspacePath (raw: string) : Result<string, string> =
    let s = raw.Trim()
    if s.Length = 0 then Result.Error "Workspace path cannot be empty"
    else Result.Ok (expandPath s)

/// Parse fallback models: comma-separated model names.
let private parseFallbackModels (raw: string) : Result<string list, string> =
    let models =
        raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s.Length > 0)
        |> Array.toList
    Result.Ok models

// ── Additional parsers for optional steps ────────────────────────────────────

let private parseTelegramToken (raw: string) : Result<TelegramBotToken, string> =
    TelegramBotToken.create (raw.Trim())

let private parseProxy (raw: string) : Result<Uri, string> =
    let s = raw.Trim()
    try Result.Ok (Uri(s, UriKind.Absolute))
    with _ -> Result.Error $"Invalid URL '{s}'. Example: http://127.0.0.1:7890"

let private parseAllowFrom (raw: string) : Result<AllowList, string> =
    let s = raw.Trim()
    if s = "*" || s.ToLower() = "anyone" || s.ToLower() = "all" then
        Result.Ok AnyoneAllowed
    else
        let ids = s.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        if ids.Length = 0 then Result.Error "Enter user IDs (comma-separated) or * for anyone"
        else Result.Ok (AllowedSet (Set.ofArray ids))

let private parseGroupPolicy (raw: string) : Result<GroupPolicy, string> =
    match raw.Trim().ToLowerInvariant() with
    | "1" | "mention" -> Result.Ok MentionPolicy
    | "2" | "open"    -> Result.Ok OpenPolicy
    | other           -> Result.Error $"Unknown policy '{other}'. Enter 'mention' (1) or 'open' (2)."

// ── Wizard state ─────────────────────────────────────────────────────────────

type private WizardState = {
    Provider       : ProviderChoice   // typed DU — no raw strings inside the wizard
    ApiKey         : ApiKey option
    Model          : string
    FallbackModels : string list
    Temperature    : float
    MaxTokens      : int
    Workspace      : string
    TelegramToken  : TelegramBotToken option
    TelegramProxy  : Uri option
    TelegramAllow  : AllowList
    TelegramGroup  : GroupPolicy
}

let private defaultState = {
    Provider       = OpenAI
    ApiKey         = None
    Model          = providerDefaultModel OpenAI
    FallbackModels = []
    Temperature    = 0.7
    MaxTokens      = 4096
    Workspace      = BotSharpConfig.defaults.WorkspacePath
    TelegramToken  = None
    TelegramProxy  = None
    TelegramAllow  = AnyoneAllowed
    TelegramGroup  = MentionPolicy
}

// ── Step functions (mutually recursive for Back navigation) ──────────────────

/// Run the onboarding wizard. Returns Some BotSharpConfig on success, None if the user quits.
let runWizard (configPath: string) : BotSharpConfig option =
    printfn """
╔══════════════════════════════════════════════╗
║        BotSharp — First-run Setup          ║
║  Type 'back' to return to the previous step  ║
║  Press Enter to accept [default]             ║
║  Type 'quit' to exit without saving          ║
╚══════════════════════════════════════════════╝"""

    let rec step1 (s: WizardState) =
        printfn "\nStep 1/7 — LLM Provider"
        printfn "  1) OpenAI   2) Anthropic   3) Gemini   4) DeepSeek   5) Ollama"
        match prompt (sprintf "Provider [%s]" (providerChoiceName s.Provider)) parseProviderChoice with
        | Quit    -> None
        | Skip    -> step2 s
        | Back    -> step1 s       // first step — stay
        | Value v ->
            // Update model default when provider changes
            let model = if s.Model = providerDefaultModel s.Provider
                        then providerDefaultModel v
                        else s.Model
            step2 { s with Provider = v; Model = model }

    and step2 (s: WizardState) =
        printfn "\nStep 2/7 — API Key (for %s)" (providerChoiceName s.Provider)
        printfn "  Leave blank to configure via environment variable later"
        match prompt "API key [skip]" parseApiKey with
        | Quit    -> None
        | Back    -> step1 s
        | Skip    -> step3 { s with ApiKey = None }
        | Value k -> step3 { s with ApiKey = Some k }

    and step3 (s: WizardState) =
        printfn "\nStep 3/7 — Model"
        match prompt (sprintf "Model [%s]" s.Model) parseModel with
        | Quit    -> None
        | Back    -> step2 s
        | Skip    -> step4 s
        | Value m -> step4 { s with Model = m }

    and step4 (s: WizardState) =
        printfn "\nStep 4/7 — Fallback models (optional)"
        printfn "  If the primary model fails (429, timeout, 5xx), try these in order."
        printfn "  Comma-separated model names, e.g.: deepseek-v4-pro, gpt-4o"
        let current = if s.FallbackModels.IsEmpty then "none" else String.concat ", " s.FallbackModels
        match prompt (sprintf "Fallback models [%s]" current) parseFallbackModels with
        | Quit    -> None
        | Back    -> step3 s
        | Skip    -> step5 s
        | Value m -> step5 { s with FallbackModels = m }

    and step5 (s: WizardState) =
        printfn "\nStep 5/7 — Temperature (0.0–2.0)"
        match prompt (sprintf "Temperature [%.1f]" s.Temperature) parseTemperature with
        | Quit    -> None
        | Back    -> step4 s
        | Skip    -> step6 s
        | Value t -> step6 { s with Temperature = t }

    and step6 (s: WizardState) =
        printfn "\nStep 6/7 — Workspace path"
        match prompt (sprintf "Workspace [%s]" s.Workspace) parseWorkspacePath with
        | Quit    -> None
        | Back    -> step5 s
        | Skip    -> step7 s
        | Value w -> step7 { s with Workspace = w }

    and step7 (s: WizardState) =
        printfn "\nStep 7/7 — Telegram bot (optional)"
        printfn "  Leave blank to skip — you can add it to config.json later."
        printfn "  Get a token from @BotFather on Telegram."
        match prompt "Bot token [skip]" parseTelegramToken with
        | Quit    -> None
        | Back    -> step6 s
        | Skip    -> finish { s with TelegramToken = None }
        | Value t ->
            // Ask allow_from
            let s1 = { s with TelegramToken = Some t }
            match prompt "Allow from (* = anyone, or comma-separated IDs) [*]" parseAllowFrom with
            | Quit    -> None
            | Back    -> step7 s
            | Skip    -> step7b { s1 with TelegramAllow = AnyoneAllowed }
            | Value a -> step7b { s1 with TelegramAllow = a }

    and step7b (s: WizardState) =
        // Group policy (only relevant if token was provided)
        printfn "\n  Group policy:  1) mention (respond only when @mentioned)  2) open (respond to all)"
        match prompt "Group policy [mention]" parseGroupPolicy with
        | Quit    -> None
        | Back    -> step7 s
        | Skip    -> step7c { s with TelegramGroup = MentionPolicy }
        | Value g -> step7c { s with TelegramGroup = g }

    and step7c (s: WizardState) =
        // Proxy (optional)
        match prompt "HTTP proxy (optional) [skip]" parseProxy with
        | Quit    -> None
        | Back    -> step7b s
        | Skip    -> finish { s with TelegramProxy = None }
        | Value p -> finish { s with TelegramProxy = Some p }

    and finish (s: WizardState) =
        // providerChoiceName: the single conversion point from typed DU to string.
        // All wizard code above this boundary uses ProviderChoice, never raw strings.
        let providerName = providerChoiceName s.Provider
        let apiKeys =
            match s.ApiKey with
            | None   -> Map.empty
            | Some k -> Map.ofList [ providerName, k ]
        let telegram =
            match s.TelegramToken with
            | None -> None
            | Some token ->
                Some {
                    Token              = token
                    AllowFrom          = s.TelegramAllow
                    Proxy              = s.TelegramProxy
                    ReplyToMessage     = false
                    ReactEmoji         = None
                    GroupPolicy        = s.TelegramGroup
                    ConnectionPoolSize = 8
                    PoolTimeout        = TimeSpan.FromSeconds(30.0)
                    Streaming          = true
                    InlineKeyboards    = false
                    StreamEditInterval = TimeSpan.FromSeconds(0.5)
                }
        let cfg = {
            BotSharpConfig.defaults with
                DefaultProvider = providerName
                DefaultModel    = s.Model
                FallbackModels  = s.FallbackModels
                Temperature     = s.Temperature
                MaxTokens       = s.MaxTokens
                WorkspacePath   = expandPath s.Workspace
                ApiKeys         = apiKeys
                Telegram        = telegram
        }
        printfn "\nSaving configuration to %s..." configPath
        match saveConfig configPath cfg |> Async.RunSynchronously with
        | Result.Ok () ->
            printfn "Configuration saved. Starting BotSharp...\n"
            Some cfg
        | Result.Error e ->
            printfn "Warning: could not save config (%s). Continuing with in-memory config." e
            Some cfg

    step1 defaultState
