module BotSharp.Infrastructure.Channels.CliChannel

open System
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Input.InputParser
open BotSharp.Infrastructure.Channels.ChannelBase
open BotSharp.Infrastructure.Tools.ToolHints

// ═══════════════════════════════════════════════════════════════════════════
// CLI channel adapter
//
// Reads from stdin and writes to stdout.  Each line of user input is parsed
// as a UserInput (slash command or chat message) and packaged into an
// InboundMessage.  The Send function prints the assistant's reply.
//
// For streaming output the AgentStreamHook prints deltas directly.
// Every OutboundMessage that reaches Send is a complete, displayable reply
// (streaming paths return StreamedResponse from AgentResult and never reach
// port.Send at all).
// ═══════════════════════════════════════════════════════════════════════════

let private cliChannel = ChannelId "cli"
let private cliUser    = UserId    "user"
let private cliChat    = ChatId    "cli-session"

/// AgentStreamHook for the CLI: print deltas directly to stdout.
let cliStreamHook : AgentStreamHook =
    StreamingHook(
        (fun text     -> async { printf "%s" text; Console.Out.Flush() }),
        (fun hasTools -> async { if not hasTools then printfn "" })
    )

/// AgentHook for the CLI: prints tool hints before each tool round.
/// `sendToolHints` — when false (the default per Python parity), hints are suppressed.
/// `isStreaming` — in streaming mode the delta stream has already written output;
/// both paths print the same line but the context differs.
let cliAgentHook (isStreaming: bool) (sendToolHints: bool) : AgentHook =
    { AgentHook.none with
        BeforeExecuteTools = fun ctx ->
            async {
                if sendToolHints then
                    let hint = formatToolHints ctx.ToolCalls
                    if hint <> "" then
                        printfn "\n[tools] %s" hint
                        Console.Out.Flush()
            } }

/// Render inline keyboard buttons as ASCII rows (CLI fallback for Telegram buttons).
let private renderButtons (buttons: string list list) : string =
    if buttons.IsEmpty then ""
    else
        let rows = buttons |> List.map (fun row -> "  [ " + String.concat " | " row + " ]")
        "\n" + String.concat "\n" rows

/// Create a ChannelPort for the CLI.
let createCliPort () : ChannelPort = {
    Send = fun msg ->
        async {
            printfn "\nassistant> %s" msg.Content
            let btnText = renderButtons msg.Buttons
            if btnText <> "" then printfn "%s" btnText
        }

    Receive =
        async {
            printf "\nyou> "
            Console.Out.Flush()
            match Console.ReadLine() with
            | null ->
                // EOF (Ctrl-D) — channel permanently closed
                return ChannelClosed
            | line ->
                let input =
                    match parseUserInput line with
                    | Result.Ok v  -> v
                    | Result.Error _ -> ChatMessage (line, [])
                return Message {
                    Channel            = cliChannel
                    Sender             = cliUser
                    Chat               = cliChat
                    Input              = input
                    Metadata           = Map.empty
                    SessionKeyOverride = None
                }
        }
}
