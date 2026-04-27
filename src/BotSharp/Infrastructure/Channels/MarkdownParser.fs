module internal BotSharp.Infrastructure.Channels.MarkdownParser

open System.Text.RegularExpressions

// ═══════════════════════════════════════════════════════════════════════════
// Markdown → Telegram HTML
//
// Converts a subset of Markdown to HTML accepted by Telegram's parseMode:
//   HTML → <b>, <i>, <s>, <code>, <pre><code>, <a href="...">, <blockquote>
//
// Transformation order (critical for correctness):
//   1. Sentinel fenced code blocks     → protect from ALL inline transforms
//   2. HTML-escape (&, <, >)
//   3. Sentinel inline code spans      → protect from bold/italic/etc.
//   4. Bold, italic, strikethrough, links
//   5. Blockquotes (line-by-line)
//   6. Restore inline code sentinels   → <code>…</code>
//   7. Restore fenced block sentinels  → <pre><code>…</code></pre>
//
// Code content is extracted BEFORE inline transforms so that constructs
// like **bold** inside a fenced block or inline `**tick**` are never
// transformed.  NUL bytes (\x00) are used as sentinels — they never appear
// in valid UTF-8 messages and are unaffected by HTML escaping.
// ═══════════════════════════════════════════════════════════════════════════

let private htmlEsc (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

// Compiled once at module load — not per call.
let private fencedRe     = Regex(@"```(?:[a-zA-Z0-9]*)\n?([\s\S]*?)```", RegexOptions.Singleline ||| RegexOptions.Compiled)
let private inlineCodeRe = Regex(@"`([^`\n]+)`",                          RegexOptions.Compiled)
let private boldRe       = Regex(@"\*\*(.+?)\*\*|__(.+?)__",             RegexOptions.Singleline ||| RegexOptions.Compiled)
let private italicRe     = Regex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(?<!_)_(?!_)(.+?)(?<!_)_(?!_)",
                                  RegexOptions.Singleline ||| RegexOptions.Compiled)
let private strikeRe     = Regex(@"~~(.+?)~~",                            RegexOptions.Singleline ||| RegexOptions.Compiled)
let private linkRe       = Regex(@"\[([^\]]+)\]\(([^)]+)\)",              RegexOptions.Compiled)

/// Converts a Markdown string to Telegram-compatible HTML.
/// Pure — no IO, no external mutable state.
let markdownToHtml (text: string) : string =
    let fencedBufs = System.Collections.Generic.List<string>()
    let codeBufs   = System.Collections.Generic.List<string>()
    let fSen i     = sprintf "\x00FENCED%d\x00" i
    let cSen i     = sprintf "\x00CODE%d\x00"   i

    // ── 1. Sentinel fenced code blocks (raw content, before HTML-escape) ──
    let s1 =
        fencedRe.Replace(text, fun m ->
            let idx = fencedBufs.Count
            fencedBufs.Add(m.Groups.[1].Value)
            fSen idx)

    // ── 2. HTML-escape everything that remains ────────────────────────────
    let s2 = htmlEsc s1

    // ── 3. Sentinel inline code spans (content already HTML-escaped) ──────
    let mutable s =
        inlineCodeRe.Replace(s2, fun m ->
            let idx = codeBufs.Count
            codeBufs.Add(m.Groups.[1].Value)
            cSen idx)

    // ── 4. Inline formatting (sentinels are opaque to all these patterns) ─
    s <- boldRe.Replace(s, fun m ->
        let v = if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value
        sprintf "<b>%s</b>" v)
    s <- italicRe.Replace(s, fun m ->
        let v = if m.Groups.[1].Success then m.Groups.[1].Value else m.Groups.[2].Value
        sprintf "<i>%s</i>" v)
    s <- strikeRe.Replace(s, fun m -> sprintf "<s>%s</s>" m.Groups.[1].Value)
    s <- linkRe.Replace(s, fun m -> sprintf "<a href=\"%s\">%s</a>" m.Groups.[2].Value m.Groups.[1].Value)

    // ── 5. Blockquotes ("> " prefix after HTML-escaping becomes "&gt; ") ─
    s <-
        s.Split('\n')
        |> Array.map (fun line ->
            if   line.StartsWith("&gt; ") then sprintf "<blockquote>%s</blockquote>" line.[5..]
            elif line.StartsWith("&gt;") && line.Length > 4 then sprintf "<blockquote>%s</blockquote>" line.[4..]
            else line)
        |> String.concat "\n"

    // ── 6. Restore inline code sentinels → <code>…</code> ────────────────
    // Content is already HTML-escaped (from step 2), so no second escaping.
    for i in 0 .. codeBufs.Count - 1 do
        s <- s.Replace(cSen i, sprintf "<code>%s</code>" codeBufs.[i])

    // ── 7. Restore fenced block sentinels → <pre><code>…</code></pre> ────
    // Content is raw (from step 1), so we HTML-escape it here.
    for i in 0 .. fencedBufs.Count - 1 do
        s <- s.Replace(fSen i, sprintf "<pre><code>%s</code></pre>" (htmlEsc fencedBufs.[i]))

    s
