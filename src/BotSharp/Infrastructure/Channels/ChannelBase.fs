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
