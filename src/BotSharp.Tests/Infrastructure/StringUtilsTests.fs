module BotSharp.Tests.Infrastructure.StringUtilsTests

open Xunit
open BotSharp.Infrastructure.Shared.StringUtils

// ═══════════════════════════════════════════════════════════════════════════
// stripThink — Python parity for nanobot.utils.helpers.strip_think
// Mirrors test_strip_think.py structure.
// ═══════════════════════════════════════════════════════════════════════════

// ── Basic well-formed tags ────────────────────────────────────────────────

[<Fact>]
let ``stripThink removes closed thought tag`` () =
    Assert.Equal("Hello  World", stripThink "Hello <thought>reasoning</thought> World")

[<Fact>]
let ``stripThink removes unclosed trailing thought tag`` () =
    Assert.Equal("", stripThink "<thought>ongoing...")

[<Fact>]
let ``stripThink removes multiline thought tag`` () =
    Assert.Equal("End", stripThink "<thought>\nline1\nline2\n</thought>End")

[<Fact>]
let ``stripThink removes thought tag containing nested angle brackets`` () =
    Assert.Equal("result", stripThink "<thought>a < 3 and b > 2</thought>result")

[<Fact>]
let ``stripThink removes multiple thought tag blocks`` () =
    Assert.Equal("ABC", stripThink "A<thought>x</thought>B<thought>y</thought>C")

[<Fact>]
let ``stripThink removes thought tag with only whitespace inside`` () =
    Assert.Equal("beforeafter", stripThink "before<thought>  </thought>after")

[<Fact>]
let ``stripThink preserves self-closing thought tag`` () =
    Assert.Equal("<thought/>some text", stripThink "<thought/>some text")

[<Fact>]
let ``stripThink leaves normal text unchanged`` () =
    Assert.Equal("Just normal text", stripThink "Just normal text")

[<Fact>]
let ``stripThink returns empty string for empty input`` () =
    Assert.Equal("", stripThink "")

// ── think-tag variants ────────────────────────────────────────────────────

[<Fact>]
let ``stripThink removes closed think tag`` () =
    Assert.Equal("Before  After", stripThink "Before <think>internal</think> After")

[<Fact>]
let ``stripThink removes unclosed think prefix`` () =
    Assert.Equal("", stripThink "<think>reasoning without closing")

[<Fact>]
let ``stripThink removes unclosed think prefix with leading whitespace`` () =
    Assert.Equal("", stripThink "  <think>reasoning...")

[<Fact>]
let ``stripThink removes unclosed thought prefix`` () =
    Assert.Equal("", stripThink "<thought>reasoning without closing")

// ── False-positive preservation (mid-text tags not at line start) ─────────

[<Fact>]
let ``stripThink preserves backtick-wrapped think tag in prose`` () =
    let text = "*Think Stripping:* A new utility to strip `<think>` tags from output."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink preserves mid-sentence think tag that is not an opener`` () =
    let text = "The model emits <think> at the start of its response."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink preserves think tag inside code block`` () =
    let text = "Example:\n```\ntext = re.sub(r\"<think>[\\s\\S]*\", \"\", text)\n```\nDone."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink preserves backtick-wrapped thought tag in prose`` () =
    let text = "Gemma 4 uses `<thought>` blocks for reasoning."
    Assert.Equal(text, stripThink text)

// ── Malformed / tokenizer leaks ───────────────────────────────────────────

[<Fact>]
let ``stripThink strips malformed think tag with no closing gt and CJK content`` () =
    Assert.Equal("广场照明灯目前绑定在'照明灯'策略下", stripThink "<think广场照明灯目前绑定在'照明灯'策略下")

[<Fact>]
let ``stripThink strips malformed think tag with space before content`` () =
    Assert.Equal("The fountain opens at 09:00", stripThink "<think The fountain opens at 09:00")

[<Fact>]
let ``stripThink strips malformed thought tag with CJK content`` () =
    Assert.Equal("广场照明灯", stripThink "<thought广场照明灯")

[<Fact>]
let ``stripThink preserves thinker variant tag`` () =
    Assert.Equal("<thinker>content</thinker>", stripThink "<thinker>content</thinker>")

[<Fact>]
let ``stripThink preserves self-closing think tag (short form)`` () =
    Assert.Equal("<think/>ok", stripThink "<think/>ok")

[<Fact>]
let ``stripThink preserves self-closing thought tag (short form)`` () =
    Assert.Equal("<thought/>ok", stripThink "<thought/>ok")

[<Fact>]
let ``stripThink strips orphan closing think tag at end of text`` () =
    Assert.Equal("answer", stripThink "answer</think>")

[<Fact>]
let ``stripThink strips orphan closing think tag at start of text`` () =
    Assert.Equal("answer", stripThink "</think>answer")

[<Fact>]
let ``stripThink strips channel marker at start`` () =
    Assert.Equal("喷泉策略：09:00 开启", stripThink "<channel|>喷泉策略：09:00 开启")

[<Fact>]
let ``stripThink strips pipe-wrapped channel marker at start`` () =
    Assert.Equal("answer", stripThink "<|channel|>answer")

// ── Conservative preservation (similar but distinct tag variants) ─────────

[<Fact>]
let ``stripThink preserves think-dash variant tag`` () =
    Assert.Equal("<think-foo>bar</think-foo>", stripThink "<think-foo>bar</think-foo>")

[<Fact>]
let ``stripThink preserves think-underscore variant tag`` () =
    Assert.Equal("<think_foo>bar</think_foo>", stripThink "<think_foo>bar</think_foo>")

[<Fact>]
let ``stripThink preserves think-numeric variant tag`` () =
    Assert.Equal("<think1>bar</think1>", stripThink "<think1>bar</think1>")

[<Fact>]
let ``stripThink preserves think-namespaced variant tag`` () =
    Assert.Equal("<think:foo>bar</think:foo>", stripThink "<think:foo>bar</think:foo>")

[<Fact>]
let ``stripThink preserves literal close-think in mid-prose`` () =
    let text = "Use `</think>` to close a thinking block."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink preserves literal channel marker in prose`` () =
    let text = "The Harmony spec uses `<|channel|>` and `<channel|>` markers."
    Assert.Equal(text, stripThink text)

[<Fact>]
let ``stripThink preserves literal channel marker inside code block`` () =
    let text = "Example:\n```\nif line.startswith('<channel|>'):\n    skip()\n```"
    Assert.Equal(text, stripThink text)
