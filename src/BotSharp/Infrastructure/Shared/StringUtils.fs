module BotSharp.Infrastructure.Shared.StringUtils

open System.Text.RegularExpressions

// ═══════════════════════════════════════════════════════════════════════════
// stripThink — remove <think>…</think> / <thought>…</thought> reasoning blocks
//
// Some reasoning models (DeepSeek R1, QwQ, Gemma 4) emit internal reasoning
// wrapped in these tags.  stripThink removes them before text is shown to the
// user or persisted to history.
// Mirrors Python nanobot.utils.helpers.strip_think exactly.
// ═══════════════════════════════════════════════════════════════════════════

/// Remove <think>…</think>, <thought>…</thought> blocks and template-level
/// leaks occasionally emitted by reasoning models (DeepSeek R1, QwQ, Gemma 4).
/// Mirrors Python nanobot.utils.helpers.strip_think.
let stripThink (text: string) : string =
    if System.String.IsNullOrEmpty text then text
    else
        let mutable t = text
        // 1. Well-formed blocks (non-greedy: stop at first closing tag).
        t <- Regex.Replace(t, @"<think>[\s\S]*?</think>", "")
        // 2. Unclosed/streaming prefix — <think> with content but no closing tag.
        t <- Regex.Replace(t, @"^\s*<think>[\s\S]*$", "")
        // 3. Well-formed <thought> blocks.
        t <- Regex.Replace(t, @"<thought>[\s\S]*?</thought>", "")
        // 4. Unclosed <thought> prefix.
        t <- Regex.Replace(t, @"^\s*<thought>[\s\S]*$", "")
        // 5. Malformed opening tags: <think / <thought where next char is not a
        //    valid XML-tag character (covers <think广场… tokenizer leaks in CJK).
        t <- Regex.Replace(t, @"<think(?![A-Za-z0-9_\-:>/])", "")
        t <- Regex.Replace(t, @"<thought(?![A-Za-z0-9_\-:>/])", "")
        // 6. Orphan closing tags at edges only (avoids silently rewriting mid-text
        //    prose that legitimately discusses these tokens).
        t <- Regex.Replace(t, @"^\s*</think>\s*", "")
        t <- Regex.Replace(t, @"\s*</think>\s*$", "")
        t <- Regex.Replace(t, @"^\s*</thought>\s*", "")
        t <- Regex.Replace(t, @"\s*</thought>\s*$", "")
        // 7. Harmony / Gemma 4 channel markers at start of text only.
        t <- Regex.Replace(t, @"^\s*<\|?channel\|?>\s*", "")
        t.Trim()
