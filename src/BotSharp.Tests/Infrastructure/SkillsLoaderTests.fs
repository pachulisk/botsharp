module BotSharp.Tests.Infrastructure.SkillsLoaderTests

open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Skills.SkillsLoader

// ═══════════════════════════════════════════════════════════════════════════
// Test helpers
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally
        try Directory.Delete(dir, recursive = true) with _ -> ()

let private createSkillFile (workspace: string) (name: string) (content: string) =
    let dir = Path.Combine(workspace, "skills", name)
    Directory.CreateDirectory(dir) |> ignore
    File.WriteAllText(Path.Combine(dir, "SKILL.md"), content)

// ═══════════════════════════════════════════════════════════════════════════
// listSkills
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listSkills returns empty list when skills dir does not exist`` () =
    withTempDir (fun workspace ->
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Empty(skills))

[<Fact>]
let ``listSkills returns empty list when skills dir is empty`` () =
    withTempDir (fun workspace ->
        Directory.CreateDirectory(Path.Combine(workspace, "skills")) |> ignore
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Empty(skills))

[<Fact>]
let ``listSkills ignores directories without SKILL.md`` () =
    withTempDir (fun workspace ->
        // Create a directory with a different file
        let dir = Path.Combine(workspace, "skills", "not-a-skill")
        Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(Path.Combine(dir, "README.md"), "hello")
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Empty(skills))

[<Fact>]
let ``listSkills loads a skill without frontmatter`` () =
    withTempDir (fun workspace ->
        createSkillFile workspace "tmux" "Use tmux for terminal multiplexing."
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        Assert.Equal("tmux", skills.[0].Name)  // falls back to dir name
        Assert.Equal(OnDemand, skills.[0].Activation))

[<Fact>]
let ``listSkills reads name and description from frontmatter`` () =
    withTempDir (fun workspace ->
        let md = "---\nname: Tmux Helper\ndescription: Manage terminal sessions\n---\nUse tmux."
        createSkillFile workspace "tmux" md
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        Assert.Equal("Tmux Helper", skills.[0].Name)
        Assert.Equal("Manage terminal sessions", skills.[0].Description))

[<Fact>]
let ``listSkills sets AlwaysActive for activation: always`` () =
    withTempDir (fun workspace ->
        let md = "---\nname: Core\ndescription: Core skill\nactivation: always\n---\nCore content."
        createSkillFile workspace "core" md
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(AlwaysActive, skills.[0].Activation))

[<Fact>]
let ``listSkills sets OnDemand for absent activation`` () =
    withTempDir (fun workspace ->
        createSkillFile workspace "weather" "---\nname: Weather\n---\nGet weather."
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(OnDemand, skills.[0].Activation))

[<Fact>]
let ``listSkills sets OnDemand for unknown activation value`` () =
    withTempDir (fun workspace ->
        let md = "---\nactivation: periodic\n---\nBody."
        createSkillFile workspace "periodic" md
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(OnDemand, skills.[0].Activation))

[<Fact>]
let ``listSkills strips frontmatter from content`` () =
    withTempDir (fun workspace ->
        let md = "---\nname: Weather\ndescription: d\n---\nActual skill content."
        createSkillFile workspace "weather" md
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal("Actual skill content.", skills.[0].Content))

[<Fact>]
let ``listSkills loads multiple skills`` () =
    withTempDir (fun workspace ->
        createSkillFile workspace "tmux"    "tmux skill"
        createSkillFile workspace "weather" "weather skill"
        createSkillFile workspace "github"  "github skill"
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(3, skills.Length))

// ═══════════════════════════════════════════════════════════════════════════
// buildSkillsSummary
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildSkillsSummary returns empty string for empty list`` () =
    Assert.Equal("", buildSkillsSummary [])

[<Fact>]
let ``buildSkillsSummary wraps output in skills element`` () =
    let skill = { Name = "tmux"; Description = "desc"; Content = "body"; Activation = OnDemand }
    let xml = buildSkillsSummary [ skill ]
    Assert.StartsWith("<skills>", xml)
    Assert.EndsWith("</skills>", xml)

[<Fact>]
let ``buildSkillsSummary includes name and description`` () =
    let skill = { Name = "tmux"; Description = "Manage terminals"; Content = "body"; Activation = OnDemand }
    let xml = buildSkillsSummary [ skill ]
    Assert.Contains("<name>tmux</name>", xml)
    Assert.Contains("<description>Manage terminals</description>", xml)

[<Fact>]
let ``buildSkillsSummary marks OnDemand activation correctly`` () =
    let skill = { Name = "s"; Description = "d"; Content = "c"; Activation = OnDemand }
    let xml = buildSkillsSummary [ skill ]
    Assert.Contains("activation=\"on_demand\"", xml)

[<Fact>]
let ``buildSkillsSummary excludes AlwaysActive skills (they are in buildAlwaysActiveContent)`` () =
    let skill = { Name = "s"; Description = "d"; Content = "always body"; Activation = AlwaysActive }
    let xml = buildSkillsSummary [ skill ]
    // Always-active skills are NOT in the summary XML — they go to buildAlwaysActiveContent.
    Assert.Equal("", xml)

[<Fact>]
let ``buildSkillsSummary does not embed content for OnDemand skills`` () =
    let skill = { Name = "s"; Description = "d"; Content = "on-demand body"; Activation = OnDemand }
    let xml = buildSkillsSummary [ skill ]
    Assert.DoesNotContain("on-demand body", xml)
    Assert.DoesNotContain("<content>", xml)

[<Fact>]
let ``buildAlwaysActiveContent formats always-active skills as markdown`` () =
    let skill = { Name = "my-skill"; Description = "d"; Content = "always body"; Activation = AlwaysActive }
    let content = buildAlwaysActiveContent [ skill ]
    Assert.Contains("### Skill: my-skill", content)
    Assert.Contains("always body", content)

[<Fact>]
let ``buildAlwaysActiveContent returns empty for on-demand skills`` () =
    let skill = { Name = "s"; Description = "d"; Content = "c"; Activation = OnDemand }
    let content = buildAlwaysActiveContent [ skill ]
    Assert.Equal("", content)

[<Fact>]
let ``buildAlwaysActiveContent joins multiple skills with separators`` () =
    let skills = [
        { Name = "a"; Description = ""; Content = "content-a"; Activation = AlwaysActive }
        { Name = "b"; Description = ""; Content = "content-b"; Activation = AlwaysActive }
    ]
    let content = buildAlwaysActiveContent skills
    Assert.Contains("content-a", content)
    Assert.Contains("content-b", content)
    Assert.Contains("---", content)

[<Fact>]
let ``buildSkillsSummary XML-escapes special characters in name and description`` () =
    let skill = { Name = "a & b"; Description = "<test>"; Content = "body"; Activation = OnDemand }
    let xml = buildSkillsSummary [ skill ]
    Assert.Contains("a &amp; b", xml)
    Assert.Contains("&lt;test&gt;", xml)

// ═══════════════════════════════════════════════════════════════════════════
// Requirements checking
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listSkills includes skill with no metadata (no requirements)`` () =
    withTempDir (fun workspace ->
        createSkillFile workspace "simple" "---\nname: Simple\n---\nBody"
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length))

[<Fact>]
let ``listSkills excludes skill whose required binary is not on PATH`` () =
    withTempDir (fun workspace ->
        // Use a binary name that definitely doesn't exist
        let content = """---
name: Needs Missing Binary
metadata: {"botsharp":{"requires":{"bins":["__botsharp_definitely_not_installed__"]}}}
---
Body"""
        createSkillFile workspace "needs-bin" content
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Empty(skills))

[<Fact>]
let ``listSkills includes skill whose required binary exists`` () =
    withTempDir (fun workspace ->
        // "sh" exists on all Unix/Mac platforms
        let content = """---
name: Needs sh
metadata: {"botsharp":{"requires":{"bins":["sh"]}}}
---
Body"""
        createSkillFile workspace "needs-sh" content
        let skills = listSkills workspace |> Async.RunSynchronously
        // sh is always available on macOS/Linux; test passes on those platforms
        if System.IO.File.Exists("/bin/sh") then
            Assert.Equal(1, skills.Length))

[<Fact>]
let ``listSkills excludes skill whose required env var is absent`` () =
    withTempDir (fun workspace ->
        let content = """---
name: Needs Env
metadata: {"botsharp":{"requires":{"env":["__BOTSHARP_TEST_ENV_DEFINITELY_NOT_SET__"]}}}
---
Body"""
        createSkillFile workspace "needs-env" content
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Empty(skills))

[<Fact>]
let ``listSkills includes skill whose required env var is present`` () =
    // HOME is virtually always set on macOS/Linux.
    withTempDir (fun workspace ->
        let content = """---
name: Needs Home
metadata: {"botsharp":{"requires":{"env":["HOME"]}}}
---
Body"""
        createSkillFile workspace "needs-home" content
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length))

[<Fact>]
let ``listSkills excludes skill when binary requirement fails even if env var is present`` () =
    withTempDir (fun workspace ->
        let content = """---
name: Both Requirements
metadata: {"botsharp":{"requires":{"bins":["__botsharp_definitely_not_installed__"],"env":["HOME"]}}}
---
Body"""
        createSkillFile workspace "both-req" content
        let skills = listSkills workspace |> Async.RunSynchronously
        // Binary missing → whole skill excluded despite env var being present
        Assert.Empty(skills))

// ═══════════════════════════════════════════════════════════════════════════
// parseFrontmatter — malformed (no closing ---) treated as body
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listSkills treats malformed frontmatter with no closing --- as body`` () =
    withTempDir (fun workspace ->
        // Opener --- but no matching close: entire file is treated as body,
        // meta map stays empty, so name falls back to the directory name.
        let md = "---\nname: Unclosed\nThis is treated as body."
        createSkillFile workspace "malformed" md
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        Assert.Equal("malformed", skills.[0].Name)
        Assert.Contains("---", skills.[0].Content))

// ═══════════════════════════════════════════════════════════════════════════
// requirementsMet — unparseable JSON and no botsharp wrapper
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listSkills includes skill with unparseable metadata JSON`` () =
    withTempDir (fun workspace ->
        // JsonException in requirementsMet → returns true → skill is included
        let content = "---\nname: Bad Meta\nmetadata: {not valid json}\n---\nBody"
        createSkillFile workspace "bad-meta" content
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        Assert.Equal("Bad Meta", skills.[0].Name))

[<Fact>]
let ``listSkills includes skill whose metadata JSON has no botsharp wrapper`` () =
    withTempDir (fun workspace ->
        // metadata without "botsharp" key → root used directly as the botsharp object
        // requires.bins: ["sh"] — sh exists on macOS/Linux → requirements met
        let content = """---
name: No Wrapper
metadata: {"requires":{"bins":["sh"]}}
---
Body"""
        createSkillFile workspace "no-wrapper" content
        let skills = listSkills workspace |> Async.RunSynchronously
        if System.IO.File.Exists("/bin/sh") then
            Assert.Equal(1, skills.Length))

// ═══════════════════════════════════════════════════════════════════════════
// buildSkillsSummary — empty description omits <description> tag
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildSkillsSummary omits description element when description is empty`` () =
    let skill = { Name = "nodesc"; Description = ""; Content = "body"; Activation = OnDemand }
    let xml = buildSkillsSummary [ skill ]
    Assert.DoesNotContain("<description>", xml)
    Assert.Contains("<name>nodesc</name>", xml)

// ═══════════════════════════════════════════════════════════════════════════
// Mixed-activation lists
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``buildSkillsSummary with mixed list only includes OnDemand skills`` () =
    let always   = { Name = "always-one"; Description = "d1"; Content = "always-body"; Activation = AlwaysActive }
    let onDemand = { Name = "demand-one"; Description = "d2"; Content = "demand-body"; Activation = OnDemand }
    let xml = buildSkillsSummary [ always; onDemand ]
    Assert.DoesNotContain("always-one", xml)
    Assert.Contains("demand-one", xml)

[<Fact>]
let ``buildAlwaysActiveContent with mixed list only includes AlwaysActive skills`` () =
    let always   = { Name = "always-one"; Description = ""; Content = "always-body"; Activation = AlwaysActive }
    let onDemand = { Name = "demand-one"; Description = ""; Content = "demand-body"; Activation = OnDemand }
    let content = buildAlwaysActiveContent [ always; onDemand ]
    Assert.Contains("always-body", content)
    Assert.DoesNotContain("demand-body", content)

// ═══════════════════════════════════════════════════════════════════════════
// parseFrontmatter — frontmatter line with empty key (k.Length = 0 branch)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listSkills skips frontmatter line with empty key (colon at position 0)`` () =
    // parseFrontmatter: line = ": orphan-value" → i=0, k = "" → k.Length=0 → None (skipped)
    // The name and description from the named line still parse correctly.
    withTempDir (fun workspace ->
        let md = "---\n: orphan-value\nname: ValidName\n---\nBody text"
        createSkillFile workspace "emptyk" md
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        // Empty-key line is filtered; the "name" key is still found.
        Assert.Equal("ValidName", skills.[0].Name))

// ═══════════════════════════════════════════════════════════════════════════
// requirementsMet / jsonStringList — non-array bins value
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``listSkills includes skill when bins metadata value is a string not an array`` () =
    // jsonStringList: el.ValueKind <> JsonValueKind.Array → returns [] → List.forall _ [] = true
    // Result: binOk=true → skill included despite bins being a JSON string, not an array.
    withTempDir (fun workspace ->
        let content = """---
name: StringBins
metadata: {"botsharp":{"requires":{"bins":"sh"}}}
---
Body"""
        createSkillFile workspace "stringbins" content
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        Assert.Equal("StringBins", skills.[0].Name))

[<Fact>]
let ``listSkills includes skill when bins array contains non-string elements`` () =
    // jsonStringList Seq.choose: e.ValueKind = JsonValueKind.Number → None (filtered)
    // → empty list → List.forall binAvailable [] = true → binOk=true → included
    withTempDir (fun workspace ->
        let content = """---
name: NumericBins
metadata: {"botsharp":{"requires":{"bins":[42, true]}}}
---
Body"""
        createSkillFile workspace "numbins" content
        let skills = listSkills workspace |> Async.RunSynchronously
        Assert.Equal(1, skills.Length)
        Assert.Equal("NumericBins", skills.[0].Name))
