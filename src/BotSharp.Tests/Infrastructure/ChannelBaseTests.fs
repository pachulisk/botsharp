module BotSharp.Tests.Infrastructure.ChannelBaseTests

open Xunit
open BotSharp.Domain.Types
open BotSharp.Infrastructure.Channels.ChannelBase
open BotSharp.Infrastructure.Input.InputParser

// ── AnyoneAllowed ────────────────────────────────────────────────────────────

[<Fact>]
let ``isAllowed AnyoneAllowed returns true for any userId`` () =
    Assert.True(isAllowed (UserId "alice") AnyoneAllowed)

[<Fact>]
let ``isAllowed AnyoneAllowed returns true for empty-string userId`` () =
    Assert.True(isAllowed (UserId "") AnyoneAllowed)

[<Fact>]
let ``isAllowed AnyoneAllowed returns true for numeric userId`` () =
    Assert.True(isAllowed (UserId "123456789") AnyoneAllowed)

// ── AllowedSet — userId present ───────────────────────────────────────────────

[<Fact>]
let ``isAllowed AllowedSet containing the userId returns true`` () =
    let allowList = AllowedSet (Set.ofList [ "alice"; "bob" ])
    Assert.True(isAllowed (UserId "alice") allowList)

[<Fact>]
let ``isAllowed AllowedSet containing the userId returns true for second member`` () =
    let allowList = AllowedSet (Set.ofList [ "alice"; "bob" ])
    Assert.True(isAllowed (UserId "bob") allowList)

// ── AllowedSet — userId absent ────────────────────────────────────────────────

[<Fact>]
let ``isAllowed AllowedSet not containing the userId returns false`` () =
    let allowList = AllowedSet (Set.ofList [ "alice"; "bob" ])
    Assert.False(isAllowed (UserId "charlie") allowList)

[<Fact>]
let ``isAllowed AllowedSet is case-sensitive`` () =
    let allowList = AllowedSet (Set.ofList [ "Alice" ])
    Assert.False(isAllowed (UserId "alice") allowList)

// ── AllowedSet — empty set ────────────────────────────────────────────────────

[<Fact>]
let ``isAllowed empty AllowedSet returns false for any userId`` () =
    let allowList = AllowedSet Set.empty
    Assert.False(isAllowed (UserId "alice") allowList)

[<Fact>]
let ``isAllowed empty AllowedSet returns false for empty-string userId`` () =
    let allowList = AllowedSet Set.empty
    Assert.False(isAllowed (UserId "") allowList)

// ── Security: exact match required — no substring or injection bypass ─────────

[<Fact>]
let ``isAllowed AllowedSet requires exact match — injection attempt with pipe is denied`` () =
    // Python parity: test_is_allowed_requires_exact_match
    // "attacker|allowed@example.com" must NOT be allowed when only "allowed@example.com" is listed.
    let allowList = AllowedSet (Set.ofList [ "allowed@example.com" ])
    Assert.False(isAllowed (UserId "attacker|allowed@example.com") allowList)

[<Fact>]
let ``isAllowed AllowedSet requires exact match — prefix bypass is denied`` () =
    let allowList = AllowedSet (Set.ofList [ "alice" ])
    Assert.False(isAllowed (UserId "alice-admin") allowList)

[<Fact>]
let ``isAllowed AllowedSet requires exact match — suffix bypass is denied`` () =
    let allowList = AllowedSet (Set.ofList [ "alice" ])
    Assert.False(isAllowed (UserId "not-alice") allowList)

// ── parseInput ────────────────────────────────────────────────────────────────

[<Fact>]
let ``parseInput "/new" returns Command NewSession`` () =
    match parseInput "/new" with
    | Command NewSession -> ()
    | other -> Assert.Fail($"Expected Command NewSession, got {other}")

[<Fact>]
let ``parseInput "/help" returns Command ShowHelp`` () =
    match parseInput "/help" with
    | Command ShowHelp -> ()
    | other -> Assert.Fail($"Expected Command ShowHelp, got {other}")

[<Fact>]
let ``parseInput "/dream" returns Command Dream`` () =
    match parseInput "/dream" with
    | Command Dream -> ()
    | other -> Assert.Fail($"Expected Command Dream, got {other}")

[<Fact>]
let ``parseInput plain text returns ChatMessage with that text`` () =
    match parseInput "hello world" with
    | ChatMessage (text, _) -> Assert.Equal("hello world", text)
    | other -> Assert.Fail($"Expected ChatMessage, got {other}")

[<Fact>]
let ``parseInput empty string returns empty ChatMessage`` () =
    match parseInput "" with
    | ChatMessage (text, _) -> Assert.Equal("", text)
    | other -> Assert.Fail($"Expected ChatMessage, got {other}")

[<Fact>]
let ``parseInput non-slash command is not confused as a command`` () =
    match parseInput "what /new means?" with
    | ChatMessage _ -> ()
    | other -> Assert.Fail($"Expected ChatMessage for non-leading slash, got {other}")
