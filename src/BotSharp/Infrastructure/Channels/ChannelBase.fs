module BotSharp.Infrastructure.Channels.ChannelBase

open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// Channel port abstraction
//
// A ChannelPort is a bidirectional communication endpoint for one channel
// adapter (CLI, HTTP webhook, WebSocket, etc.).
//
// Send  — push an outbound message to the user
// Receive — await the next inbound message from the user (None = channel closed)
// ═══════════════════════════════════════════════════════════════════════════

type ChannelPort = {
    Send    : OutboundMessage -> Async<unit>
    Receive : Async<ReceiveResult>
}

/// Check if a sender is permitted by the parsed allow-list.
/// Delegates to AllowList.permits so channel adapters never see raw strings.
let isAllowed (sender: UserId) (allowList: AllowList) : bool =
    AllowList.permits sender allowList

/// Parse user input text into a UserInput (Command or ChatMessage).
/// All channels should use this to enable slash commands (/new, /model, etc.).
let parseInput (text: string) : UserInput =
    match BotSharp.Infrastructure.Input.InputParser.parseUserInput text with
    | Result.Ok v  -> v
    | Result.Error _ -> ChatMessage (text, [])
