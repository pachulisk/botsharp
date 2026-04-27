module BotSharp.Infrastructure.Skills.SkillsLoader

open System
open System.IO
open System.Text.Json
open BotSharp.Domain.Types

// ═══════════════════════════════════════════════════════════════════════════
// SkillsLoader — scan {workspace}/skills/ for Skill.md files
//
// Layout on disk:
//   {workspace}/skills/
//     tmux/SKILL.md
//     weather/SKILL.md
//     ...
//
// Each SKILL.md may begin with a YAML-style frontmatter block:
//   ---
//   name: human-readable name (optional — falls back to directory name)
//   description: one-line description
//   activation: always    ← AlwaysActive; absent or anything else → OnDemand
//   ---
//   <skill body>
//
// Type-driven design: SkillActivation DU makes "always active vs on-demand"
// structurally distinct — no string comparisons after the boundary.
// ═══════════════════════════════════════════════════════════════════════════

// ── Frontmatter parser ────────────────────────────────────────────────────

/// Parse the leading ---…--- block from markdown. Returns (metadata map, body).
/// Keys are lowercased; values are trimmed. If no frontmatter, map is empty.
let private parseFrontmatter (content: string) : Map<string, string> * string =
    if not (content.StartsWith("---", StringComparison.Ordinal)) then
        Map.empty, content
    else
        let rest = content.[3..]
        match rest.IndexOf("\n---", StringComparison.Ordinal) with
        | -1 -> Map.empty, content   // malformed — treat entire file as body
        | idx ->
            let block = rest.[..idx - 1]
            let body  = rest.[idx + 4..].TrimStart('\n', '\r')
            let meta =
                block.Split('\n')
                |> Array.choose (fun line ->
                    match line.IndexOf(':') with
                    | -1 -> None
                    | i  ->
                        let k = line.[..i-1].Trim().ToLowerInvariant()
                        let v = line.[i+1..].Trim().Trim('"').Trim('\'')
                        if k.Length > 0 then Some (k, v) else None)
                |> Map.ofArray
            meta, body

/// Parse SkillActivation from the "activation" frontmatter value.
let private parseActivation (meta: Map<string, string>) : SkillActivation =
    match meta.TryFind "activation" with
    | Some v when v.ToLowerInvariant() = "always" -> AlwaysActive
    | _                                             -> OnDemand

// ── Requirements checking ─────────────────────────────────────────────────

/// Navigate a JsonElement path like ["botsharp"; "requires"; "bins"].
/// Returns None if any segment is absent or not an object.
let private getJsonPath (el: JsonElement) (segments: string list) : JsonElement option =
    let rec go (cur: JsonElement) = function
        | [] -> Some cur
        | (seg: string) :: rest ->
            match cur.TryGetProperty(seg) with
            | true, next -> go next rest
            | false, _   -> None
    go el segments

/// Extract a string list from a JsonElement if it's a JSON array of strings.
let private jsonStringList (el: JsonElement) : string list =
    if el.ValueKind <> JsonValueKind.Array then []
    else
        el.EnumerateArray()
        |> Seq.choose (fun e ->
            if e.ValueKind = JsonValueKind.String then Some (e.GetString() |> Option.ofObj |> Option.defaultValue "")
            else None)
        |> Seq.filter (fun s -> s.Length > 0)
        |> Seq.toList

/// Check if a binary is available on the system PATH.
let private binAvailable (name: string) : bool =
    // Search PATH directories for the binary, like `which`.
    let sep = if IO.Path.PathSeparator = ':' then ':' else ';'
    let pathEnv = Environment.GetEnvironmentVariable("PATH") |> Option.ofObj |> Option.defaultValue ""
    pathEnv.Split(sep)
    |> Array.exists (fun dir ->
        let candidate = Path.Combine(dir.Trim(), name)
        File.Exists(candidate))

/// Parse the `metadata` frontmatter field as JSON and check if all BotSharp
/// requirements (bins, env vars) are satisfied.
/// Returns true when the skill is available (requirements met or unspecified).
let private requirementsMet (meta: Map<string, string>) : bool =
    match meta.TryFind "metadata" with
    | None -> true   // no metadata → no requirements → available
    | Some raw ->
        try
            use doc = JsonDocument.Parse(raw)
            let root = doc.RootElement
            // The metadata is {"botsharp": {...}} or just {...}
            let botsharp =
                match root.TryGetProperty("botsharp") with
                | true, nb -> nb
                | false, _ -> root
            // requires.bins
            let binOk =
                match getJsonPath botsharp ["requires"; "bins"] with
                | None    -> true
                | Some el ->
                    jsonStringList el |> List.forall binAvailable
            // requires.env
            let envOk =
                match getJsonPath botsharp ["requires"; "env"] with
                | None    -> true
                | Some el ->
                    jsonStringList el
                    |> List.forall (fun v ->
                        Environment.GetEnvironmentVariable(v) |> isNull |> not)
            binOk && envOk
        with :? JsonException ->
            true   // unparseable metadata — treat as available

// ── I/O ───────────────────────────────────────────────────────────────────

let private skillsDir (workspacePath: string) =
    Path.Combine(workspacePath, "skills")

/// Try to load one skill from its directory.
/// Returns None if SKILL.md is absent or the skill's requirements are not met.
let private tryLoadSkillDir (dirPath: string) : Skill option =
    let skillFile = Path.Combine(dirPath, "SKILL.md")
    if not (File.Exists skillFile) then None
    else
        let raw          = File.ReadAllText(skillFile)
        let meta, body   = parseFrontmatter raw
        // Skip skills whose required binaries or env vars are missing.
        if not (requirementsMet meta) then None
        else
            let dirName      = Path.GetFileName(dirPath) |> Option.ofObj |> Option.defaultValue dirPath
            let name         = meta.TryFind "name" |> Option.defaultValue dirName
            let description  = meta.TryFind "description" |> Option.defaultValue ""
            let activation   = parseActivation meta
            Some {
                Name        = name
                Description = description
                Content     = body
                Activation  = activation
            }

/// Load all skills from {workspace}/skills/. Returns empty list if absent.
/// Each subdirectory containing a SKILL.md becomes one Skill.
let listSkills (workspacePath: string) : Async<Skill list> =
    async {
        let dir = skillsDir workspacePath
        if not (Directory.Exists dir) then return []
        else
            return
                Directory.GetDirectories(dir)
                |> Array.choose tryLoadSkillDir
                |> Array.toList
    }

// ── Context formatting ────────────────────────────────────────────────────

/// Escape XML special characters in a string.
let private xmlEscape (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

/// Format always-active skills for inline injection into the system prompt.
/// Each skill appears as "### Skill: {name}\n\n{content}" separated by "---".
/// Returns empty string when no always-active skills exist.
/// Mirrors Python's SkillsLoader.load_skills_for_context format.
let buildAlwaysActiveContent (skills: Skill list) : string =
    let always = skills |> List.filter (fun s -> s.Activation = AlwaysActive)
    if always.IsEmpty then ""
    else
        always
        |> List.map (fun s -> $"### Skill: {s.Name}\n\n{s.Content.Trim()}")
        |> String.concat "\n\n---\n\n"

/// Build an XML summary of on-demand skills for inclusion in the system prompt.
/// Only on-demand skills are shown (always-active are injected inline via buildAlwaysActiveContent).
/// Shows name + description so the agent can read full content via read_file.
/// Returns empty string when no on-demand skills exist.
let buildSkillsSummary (skills: Skill list) : string =
    let onDemand = skills |> List.filter (fun s -> s.Activation = OnDemand)
    if onDemand.IsEmpty then ""
    else
        let lines = ResizeArray<string>()
        lines.Add("<skills>")
        for s in onDemand do
            lines.Add($"  <skill activation=\"on_demand\">")
            lines.Add($"    <name>{xmlEscape s.Name}</name>")
            if s.Description.Length > 0 then
                lines.Add($"    <description>{xmlEscape s.Description}</description>")
            lines.Add("  </skill>")
        lines.Add("</skills>")
        String.concat "\n" lines
