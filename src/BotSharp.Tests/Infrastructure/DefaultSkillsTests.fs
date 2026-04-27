module BotSharp.Tests.Infrastructure.DefaultSkillsTests

open System.IO
open Xunit
open BotSharp.Infrastructure.Skills.DefaultSkills

// ── Helpers ───────────────────────────────────────────────────────────────

let private withTempWorkspace (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"defaultskills-test-{System.Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// installDefaults — creates skill files when absent
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``installDefaults creates memory SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "memory", "SKILL.md")
        Assert.True(File.Exists path, $"Expected memory/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults creates my SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "my", "SKILL.md")
        Assert.True(File.Exists path, $"Expected my/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults memory SKILL.md has always activation`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "memory", "SKILL.md"))
        Assert.Contains("activation: always", content)
    )

[<Fact>]
let ``installDefaults my SKILL.md has always activation`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "my", "SKILL.md"))
        Assert.Contains("activation: always", content)
    )

[<Fact>]
let ``installDefaults does not overwrite existing SKILL.md`` () =
    withTempWorkspace (fun wp ->
        let dir  = Path.Combine(wp, "skills", "memory")
        Directory.CreateDirectory(dir) |> ignore
        let path = Path.Combine(dir, "SKILL.md")
        let custom = "# custom content"
        File.WriteAllText(path, custom)
        installDefaults wp
        let after = File.ReadAllText(path)
        Assert.Equal(custom, after)
    )

[<Fact>]
let ``installDefaults is idempotent`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content1 = File.ReadAllText(Path.Combine(wp, "skills", "memory", "SKILL.md"))
        installDefaults wp   // second call should not change content
        let content2 = File.ReadAllText(Path.Combine(wp, "skills", "memory", "SKILL.md"))
        Assert.Equal(content1, content2)
    )

[<Fact>]
let ``installDefaults creates skills subdirectory when missing`` () =
    withTempWorkspace (fun wp ->
        // skills/ does not exist yet — installDefaults should create it
        let skillsDir = Path.Combine(wp, "skills")
        Assert.False(Directory.Exists skillsDir, "skills/ should not exist yet")
        installDefaults wp
        Assert.True(Directory.Exists skillsDir, "skills/ should have been created")
    )

// ═══════════════════════════════════════════════════════════════════════════
// installDefaults — new built-in skills (cron, github, tmux, weather,
//                   clawhub, summarize, skill-creator)
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``installDefaults creates cron SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "cron", "SKILL.md")
        Assert.True(File.Exists path, $"Expected cron/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults cron SKILL.md contains cron tool example`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "cron", "SKILL.md"))
        Assert.Contains("cron(action=", content)
        Assert.Contains("schedule=", content)
    )

[<Fact>]
let ``installDefaults creates github SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "github", "SKILL.md")
        Assert.True(File.Exists path, $"Expected github/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults github SKILL.md contains gh CLI examples`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "github", "SKILL.md"))
        Assert.Contains("gh pr", content)
        Assert.Contains("gh run", content)
    )

[<Fact>]
let ``installDefaults creates tmux SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "tmux", "SKILL.md")
        Assert.True(File.Exists path, $"Expected tmux/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults tmux SKILL.md contains send-keys example`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "tmux", "SKILL.md"))
        Assert.Contains("send-keys", content)
        Assert.Contains("SOCKET", content)
    )

[<Fact>]
let ``installDefaults creates weather SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "weather", "SKILL.md")
        Assert.True(File.Exists path, $"Expected weather/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults weather SKILL.md mentions wttr.in`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "weather", "SKILL.md"))
        Assert.Contains("wttr.in", content)
    )

[<Fact>]
let ``installDefaults creates clawhub SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "clawhub", "SKILL.md")
        Assert.True(File.Exists path, $"Expected clawhub/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults clawhub SKILL.md contains npx install command`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "clawhub", "SKILL.md"))
        Assert.Contains("npx --yes clawhub@latest install", content)
    )

[<Fact>]
let ``installDefaults creates summarize SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "summarize", "SKILL.md")
        Assert.True(File.Exists path, $"Expected summarize/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults summarize SKILL.md mentions YouTube`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "summarize", "SKILL.md"))
        Assert.Contains("YouTube", content)
    )

[<Fact>]
let ``installDefaults creates skill-creator SKILL.md`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let path = Path.Combine(wp, "skills", "skill-creator", "SKILL.md")
        Assert.True(File.Exists path, $"Expected skill-creator/SKILL.md to exist at {path}")
    )

[<Fact>]
let ``installDefaults skill-creator SKILL.md mentions SKILL.md frontmatter`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let content = File.ReadAllText(Path.Combine(wp, "skills", "skill-creator", "SKILL.md"))
        Assert.Contains("frontmatter", content)
        Assert.Contains("description", content)
    )

[<Fact>]
let ``installDefaults installs all nine built-in skills`` () =
    withTempWorkspace (fun wp ->
        installDefaults wp
        let expected = [ "memory"; "my"; "cron"; "github"; "tmux"; "weather"; "clawhub"; "summarize"; "skill-creator" ]
        for name in expected do
            let path = Path.Combine(wp, "skills", name, "SKILL.md")
            Assert.True(File.Exists path, $"Missing built-in skill: {name}/SKILL.md")
    )
