module BotSharp.Tests.Infrastructure.GitBlameTests

open System
open System.IO
open Xunit
open BotSharp.Infrastructure.Shared.GitBlame

// ═══════════════════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════════════════

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), $"gitblame-{Guid.NewGuid():N}")
    Directory.CreateDirectory(dir) |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

// ═══════════════════════════════════════════════════════════════════════════
// lineAges — no .git directory
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``lineAges returns empty when no .git directory exists`` () =
    withTempDir (fun dir ->
        let result = lineAges dir "memory/MEMORY.md"
        Assert.Empty(result))

[<Fact>]
let ``lineAges returns empty when file does not exist`` () =
    withTempDir (fun dir ->
        // Create a .git directory stub (not a real repo)
        Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
        let result = lineAges dir "memory/MEMORY.md"
        Assert.Empty(result))

[<Fact>]
let ``lineAges returns empty when file is empty`` () =
    withTempDir (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, ".git")) |> ignore
        let memDir = Path.Combine(dir, "memory")
        Directory.CreateDirectory(memDir) |> ignore
        File.WriteAllText(Path.Combine(memDir, "MEMORY.md"), "")
        let result = lineAges dir "memory/MEMORY.md"
        Assert.Empty(result))

// ═══════════════════════════════════════════════════════════════════════════
// annotateContent — pure function; no git required
// ═══════════════════════════════════════════════════════════════════════════

[<Fact>]
let ``annotateContent returns content unchanged when ages is empty`` () =
    let content = "## Section\n- item 1\n- item 2\n"
    let result = annotateContent content []
    Assert.Equal(content, result)

[<Fact>]
let ``annotateContent returns content unchanged when line count mismatches age count`` () =
    // 3 lines but only 1 age — mismatch guard fires.
    let content = "line 1\nline 2\nline 3"
    let ages    = [ { AgeDays = 30 } ]
    let result  = annotateContent content ages
    Assert.Equal(content, result)

[<Fact>]
let ``annotateContent does not annotate fresh lines (age <= threshold)`` () =
    let content = "## Section\n- item 1\n"
    // Two lines (trailing \n stripped for age matching), both fresh (3 days)
    let ages = [ { AgeDays = 3 }; { AgeDays = 3 } ]
    let result = annotateContent content ages
    Assert.DoesNotContain("←", result)
    Assert.Contains("## Section", result)

[<Fact>]
let ``annotateContent annotates stale non-blank lines with arrow and day count`` () =
    let content = "## Section\n- old item\n"
    let ages = [ { AgeDays = 30 }; { AgeDays = 30 } ]
    let result = annotateContent content ages
    Assert.Contains("← 30d", result)

[<Fact>]
let ``annotateContent does not annotate blank lines`` () =
    let content = "## Section\n\n- item\n"
    // 3 lines: header, blank, item — blank should not get arrow suffix
    let ages = [ { AgeDays = 30 }; { AgeDays = 30 }; { AgeDays = 30 } ]
    let result = annotateContent content ages
    let lines = result.Split('\n')
    let blankLine = lines |> Array.tryFind (fun l -> l.Trim() = "")
    Assert.True(blankLine.IsSome, "blank line must still be present")
    Assert.DoesNotContain("←", blankLine.Value)

[<Fact>]
let ``annotateContent preserves trailing newline when present`` () =
    let content = "## Section\n- item\n"
    let ages = [ { AgeDays = 5 }; { AgeDays = 5 } ]
    let result = annotateContent content ages
    Assert.True(result.EndsWith("\n"), "trailing newline must be preserved")

[<Fact>]
let ``annotateContent does not add trailing newline when absent`` () =
    let content = "## Section\n- item"
    let ages = [ { AgeDays = 5 }; { AgeDays = 5 } ]
    let result = annotateContent content ages
    Assert.False(result.EndsWith("\n"), "trailing newline must not be added")

[<Fact>]
let ``annotateContent exact threshold: age = staleThresholdDays is not annotated`` () =
    // Exactly at threshold — should NOT be annotated (only strictly > threshold).
    let content = "line at threshold"
    let ages = [ { AgeDays = staleThresholdDays } ]
    let result = annotateContent content ages
    Assert.DoesNotContain("←", result)

[<Fact>]
let ``annotateContent just-above threshold: age = staleThresholdDays + 1 is annotated`` () =
    let content = "stale line"
    let ages = [ { AgeDays = staleThresholdDays + 1 } ]
    let result = annotateContent content ages
    Assert.Contains("←", result)

[<Fact>]
let ``annotateContent mixed fresh and stale lines`` () =
    let content = "## Fresh\n- recent\n## Old\n- stale content\n"
    // 4 lines: fresh (5d), fresh (5d), stale (30d), stale (30d)
    let ages = [
        { AgeDays = 5  }
        { AgeDays = 5  }
        { AgeDays = 30 }
        { AgeDays = 30 }
    ]
    let result = annotateContent content ages
    let lines = result.Split('\n') |> Array.filter (fun l -> l <> "")
    // Fresh lines (0, 1) should not have arrows
    Assert.DoesNotContain("←", lines.[0])
    Assert.DoesNotContain("←", lines.[1])
    // Stale lines (2, 3) should have arrows
    Assert.Contains("← 30d", lines.[2])
    Assert.Contains("← 30d", lines.[3])
