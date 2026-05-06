module BotSharp.Tests.Application.Phase2ConsolidatorTests

open System
open System.IO
open Xunit
open BotSharp.Domain.Types
open BotSharp.Application.Phase2Consolidator
open BotSharp.Infrastructure.Storage.StateDb
open BotSharp.Infrastructure.Storage.JobQueue

// ═══════════════════════════════════════════════════════════════════════════
// Phase2Consolidator unit tests
//
// syncPhase2Inputs is tested against real temp directories.
// enqueuePhase2 is tested against real file-based SQLite.
// ═══════════════════════════════════════════════════════════════════════════

/// Create a real file-based StateDb.
let private mkDb () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let factory = init tmp |> Async.RunSynchronously
    (factory, tmp)

/// Build a minimal Stage1Output.
let private mkOutput (sessionId: string) (rawMemory: string) (summary: string) (slug: string option) (channel: string option) : Stage1Output =
    { SessionId                        = sessionId
      SourceUpdatedAt                  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      RawMemory                        = rawMemory
      RolloutSummary                   = summary
      RolloutSlug                      = slug
      GeneratedAt                      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      Cwd                              = None
      Channel                          = channel
      UsageCount                       = 0
      LastUsage                        = None
      SelectedForPhase2                = true
      SelectedForPhase2SourceUpdatedAt = None }

// ── syncPhase2Inputs ──────────────────────────────────────────────────────

[<Fact>]
let ``syncPhase2Inputs creates rollout_summaries directory`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let output = mkOutput "cli:s1" "raw" "summary" (Some "deploy") None
        syncPhase2Inputs tmp [ output ] |> Async.RunSynchronously
        Assert.True(Directory.Exists(Path.Combine(tmp, "rollout_summaries")))
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs writes one rollout_summary file per output`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let o1 = mkOutput "cli:s1" "raw1" "summary1" (Some "task1") None
        let o2 = mkOutput "cli:s2" "raw2" "summary2" (Some "task2") None
        syncPhase2Inputs tmp [ o1; o2 ] |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md")
        Assert.Equal(2, files.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs rollout_summary file contains session_id and summary text`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let output = mkOutput "cli:my-session" "raw memory content" "Session summary text" (Some "my_task") (Some "cli")
        syncPhase2Inputs tmp [ output ] |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md")
        Assert.Equal(1, files.Length)
        let content = File.ReadAllText(files.[0])
        Assert.Contains("cli:my-session", content)
        Assert.Contains("Session summary text", content)
        Assert.Contains("channel: cli", content)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs writes raw_memories.md`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let output = mkOutput "cli:s1" "raw memory content here" "summary" (Some "slug") None
        syncPhase2Inputs tmp [ output ] |> Async.RunSynchronously
        let rawPath = Path.Combine(tmp, "raw_memories.md")
        Assert.True(File.Exists(rawPath))
        let content = File.ReadAllText(rawPath)
        Assert.Contains("cli:s1", content)
        Assert.Contains("raw memory content here", content)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs raw_memories.md contains all outputs`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let o1 = mkOutput "cli:alpha" "raw_alpha" "sum_alpha" None None
        let o2 = mkOutput "cli:beta"  "raw_beta"  "sum_beta"  None None
        syncPhase2Inputs tmp [ o1; o2 ] |> Async.RunSynchronously
        let content = File.ReadAllText(Path.Combine(tmp, "raw_memories.md"))
        Assert.Contains("cli:alpha", content)
        Assert.Contains("raw_alpha", content)
        Assert.Contains("cli:beta", content)
        Assert.Contains("raw_beta", content)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs deletes stale rollout_summary files on second call`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        // First sync: two outputs
        let o1 = mkOutput "cli:s1" "r1" "sum1" (Some "slug1") None
        let o2 = mkOutput "cli:s2" "r2" "sum2" (Some "slug2") None
        syncPhase2Inputs tmp [ o1; o2 ] |> Async.RunSynchronously
        let after1stSync = Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md").Length
        Assert.Equal(2, after1stSync)

        // Second sync: only one output
        syncPhase2Inputs tmp [ o1 ] |> Async.RunSynchronously
        let after2ndSync = Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md").Length
        Assert.Equal(1, after2ndSync)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs handles empty output list`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        syncPhase2Inputs tmp [] |> Async.RunSynchronously
        // Should create rollout_summaries dir with no files
        Assert.True(Directory.Exists(Path.Combine(tmp, "rollout_summaries")))
        Assert.Equal(0, Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md").Length)
        // raw_memories.md should still be written
        Assert.True(File.Exists(Path.Combine(tmp, "raw_memories.md")))
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs sanitizes slug with special characters in filename`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        // Slug with spaces and special chars that should be sanitized to underscores
        let output = mkOutput "cli:spec-session" "raw" "summary" (Some "my task/with spaces") None
        syncPhase2Inputs tmp [ output ] |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md")
        Assert.Equal(1, files.Length)
        // Filename should not contain '/' or spaces — those are replaced with '_'
        let filename = Path.GetFileName(files.[0])
        Assert.DoesNotContain("/", filename)
        Assert.DoesNotContain(" ", filename)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``syncPhase2Inputs filename contains slug text`` () =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    try
        let output = mkOutput "cli:slug-test" "raw" "summary" (Some "deploy_service") None
        syncPhase2Inputs tmp [ output ] |> Async.RunSynchronously
        let files = Directory.GetFiles(Path.Combine(tmp, "rollout_summaries"), "*.md")
        Assert.Equal(1, files.Length)
        let filename = Path.GetFileName(files.[0])
        Assert.Contains("deploy_service", filename)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

// ── enqueuePhase2 ─────────────────────────────────────────────────────────

[<Fact>]
let ``enqueuePhase2 creates a pending phase2 job in the database`` () =
    let openDb, tmp = mkDb ()
    try
        use conn = openDb ()
        let watermark = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        enqueuePhase2 conn watermark
        let jobs = listJobs conn JobKind.MemoryPhase2 (Some "pending") 10 |> Async.RunSynchronously
        Assert.Equal(1, jobs.Length)
        Assert.Equal(JobKind.MemoryPhase2, jobs.[0].Kind)
    finally
        try Directory.Delete(tmp, true) with _ -> ()

[<Fact>]
let ``enqueuePhase2 called twice produces only one job row (upsert semantics)`` () =
    let openDb, tmp = mkDb ()
    try
        use conn = openDb ()
        let t1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let t2 = t1 + 1000L
        enqueuePhase2 conn t1
        enqueuePhase2 conn t2
        let jobs = listJobs conn JobKind.MemoryPhase2 None 10 |> Async.RunSynchronously
        // Upsert: should still be exactly one row
        Assert.Equal(1, jobs.Length)
    finally
        try Directory.Delete(tmp, true) with _ -> ()
