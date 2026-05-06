module BotSharp.Infrastructure.Memory.CitationParser

open System

// ═══════════════════════════════════════════════════════════════════════════
// Citation parser for progressive memory disclosure
//
// Parses <mem-citation> blocks from agent output and extracts structured
// citation entries for usage tracking.
//
// Mirrors Codex memories/read/src/citations.rs (86 lines).
//
// Format:
//   <mem-citation>
//   MEMORY.md:12-15|note=[user prefers dark theme]
//   rollout_summaries/file.md:3-8|note=[deploy procedure]
//   </mem-citation>
// ═══════════════════════════════════════════════════════════════════════════

/// A single citation entry referencing a memory file location.
/// Corresponds to Codex MemoryCitationEntry (protocol/src/memory_citation.rs).
type CitationEntry = {
    Path      : string       // relative to memory/ (e.g. "MEMORY.md")
    LineStart : int
    LineEnd   : int
    Note      : string       // brief usage description
}

/// Parsed citation block from agent output.
type MemoryCitation = {
    Entries : CitationEntry list
}

let private startTag = "<mem-citation>"
let private endTag   = "</mem-citation>"

/// Parse a single citation line.
/// Format: path:start-end|note=[description]
/// Corresponds to Codex citations.rs parse_memory_citation_entry (lines 53-70).
let private parseCitationLine (line: string) : CitationEntry option =
    let line = line.Trim()
    if String.IsNullOrWhiteSpace line then None
    else
        match line.LastIndexOf("|note=[") with
        | -1 -> None
        | noteStart ->
            let location = line.[..noteStart - 1]
            let noteRaw = line.[noteStart + 7..]
            let note = noteRaw.TrimEnd(']').Trim()
            match location.LastIndexOf(':') with
            | -1 -> None
            | colonIdx ->
                let path = location.[..colonIdx - 1]
                let range = location.[colonIdx + 1..]
                match range.Split('-') with
                | [| s; e |] ->
                    match Int32.TryParse(s.Trim()), Int32.TryParse(e.Trim()) with
                    | (true, ls), (true, le) ->
                        Some { Path = path; LineStart = ls; LineEnd = le; Note = note }
                    | _ -> None
                | _ -> None

/// Parse a <mem-citation> block from agent output.
/// Returns None if no valid citation block is found.
/// Corresponds to Codex citations.rs parse_memory_citation.
let parseCitation (text: string) : MemoryCitation option =
    match text.IndexOf(startTag), text.IndexOf(endTag) with
    | s, e when s >= 0 && e > s ->
        let block = text.[s + startTag.Length .. e - 1].Trim()
        let entries =
            block.Split('\n')
            |> Array.choose parseCitationLine
            |> Array.toList
        if entries.IsEmpty then None
        else Some { Entries = entries }
    | _ -> None

/// Strip the citation block from agent output, returning (visible text, citation).
/// Corresponds to Codex strip_citations.
let stripCitation (text: string) : string * MemoryCitation option =
    match text.IndexOf(startTag), text.IndexOf(endTag) with
    | s, e when s >= 0 && e > s ->
        let before = text.[..s - 1].TrimEnd()
        let after  = text.[e + endTag.Length..]
        let visible = (before + after).Trim()
        let citation = parseCitation text
        (visible, citation)
    | _ -> (text, None)
