module BotSharp.Tests.Channels.MarkdownParserTests

open Xunit
open BotSharp.Infrastructure.Channels.MarkdownParser

// ─────────────────────────────────────────────────────────────────────────────
// Plain text / HTML escaping
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``plain text passes through unchanged`` () =
    Assert.Equal("hello world", markdownToHtml "hello world")

[<Fact>]
let ``HTML special chars are escaped`` () =
    let result = markdownToHtml "5 < 10 & x > 0"
    Assert.Equal("5 &lt; 10 &amp; x &gt; 0", result)

// ─────────────────────────────────────────────────────────────────────────────
// Fenced code blocks
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``fenced code block with language tag renders pre code`` () =
    let input = "```python\nprint('hello')\n```"
    let result = markdownToHtml input
    Assert.Contains("<pre><code>", result)
    Assert.Contains("print('hello')", result)
    Assert.Contains("</code></pre>", result)

[<Fact>]
let ``fenced code block content is HTML-escaped`` () =
    let input = "```\n<script>alert(1)</script>\n```"
    let result = markdownToHtml input
    Assert.Contains("&lt;script&gt;", result)
    Assert.DoesNotContain("<script>", result)

[<Fact>]
let ``bold markers inside fenced block are not transformed`` () =
    let input = "```\n**not bold**\n```"
    let result = markdownToHtml input
    Assert.Contains("**not bold**", result)
    Assert.DoesNotContain("<b>", result)

// ─────────────────────────────────────────────────────────────────────────────
// Inline code
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``inline code renders code tag`` () =
    Assert.Equal("use <code>map</code> here", markdownToHtml "use `map` here")

// ─────────────────────────────────────────────────────────────────────────────
// Bold and italic
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``double-asterisk bold renders b tag`` () =
    Assert.Equal("<b>bold</b>", markdownToHtml "**bold**")

[<Fact>]
let ``double-underscore bold renders b tag`` () =
    Assert.Equal("<b>bold</b>", markdownToHtml "__bold__")

[<Fact>]
let ``single-asterisk italic renders i tag`` () =
    Assert.Equal("<i>italic</i>", markdownToHtml "*italic*")

[<Fact>]
let ``single-underscore italic renders i tag`` () =
    Assert.Equal("<i>italic</i>", markdownToHtml "_italic_")

// ─────────────────────────────────────────────────────────────────────────────
// Strikethrough
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``double-tilde strikethrough renders s tag`` () =
    Assert.Equal("<s>old</s>", markdownToHtml "~~old~~")

// ─────────────────────────────────────────────────────────────────────────────
// Links
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``markdown link renders anchor tag`` () =
    let result = markdownToHtml "[Anthropic](https://anthropic.com)"
    Assert.Equal("""<a href="https://anthropic.com">Anthropic</a>""", result)

// ─────────────────────────────────────────────────────────────────────────────
// Blockquotes
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``line starting with >space becomes blockquote`` () =
    let result = markdownToHtml "> quoted text"
    Assert.Equal("<blockquote>quoted text</blockquote>", result)

[<Fact>]
let ``non-blockquote line is unchanged`` () =
    let result = markdownToHtml "normal line"
    Assert.Equal("normal line", result)

// ─────────────────────────────────────────────────────────────────────────────
// Mixed / multi-element
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``bold and inline code in same string`` () =
    let result = markdownToHtml "Use **bold** and `code`"
    Assert.Equal("Use <b>bold</b> and <code>code</code>", result)

[<Fact>]
let ``multiple fenced blocks are handled independently`` () =
    let input = "```\nfirst\n```\n\n```\nsecond\n```"
    let result = markdownToHtml input
    let firstIdx  = result.IndexOf("first")
    let secondIdx = result.IndexOf("second")
    Assert.True(firstIdx >= 0,  "first block missing")
    Assert.True(secondIdx > firstIdx, "second block should appear after first")

[<Fact>]
let ``empty string returns empty string`` () =
    Assert.Equal("", markdownToHtml "")

[<Fact>]
let ``line starting with > without space still becomes blockquote`` () =
    // ">text" (no space) triggers the second elif branch in the blockquote logic.
    let result = markdownToHtml ">text"
    Assert.Equal("<blockquote>text</blockquote>", result)

[<Fact>]
let ``HTML ampersand is escaped`` () =
    let result = markdownToHtml "A & B"
    Assert.Equal("A &amp; B", result)

[<Fact>]
let ``code content with HTML special chars is preserved correctly`` () =
    // `<div>` inside inline code should be HTML-escaped inside <code>
    let result = markdownToHtml "`<div>`"
    Assert.Equal("<code>&lt;div&gt;</code>", result)

[<Fact>]
let ``fenced code block without language tag renders pre code`` () =
    // Triple-backtick with no language identifier — regex uses (?:[a-zA-Z0-9]*)
    // so zero characters is valid.
    let result = markdownToHtml "```\nhello world\n```"
    Assert.Contains("<pre><code>", result)
    Assert.Contains("hello world",  result)
    Assert.Contains("</code></pre>", result)

[<Fact>]
let ``multiple links in one paragraph are all converted`` () =
    let result = markdownToHtml "[A](http://a.example.com) and [B](http://b.example.com)"
    Assert.Contains("""<a href="http://a.example.com">A</a>""", result)
    Assert.Contains("""<a href="http://b.example.com">B</a>""", result)

[<Fact>]
let ``inline code takes precedence over bold inside code span`` () =
    // The ** inside a code span must not be expanded to <b>.
    let result = markdownToHtml "`**not bold**`"
    Assert.Contains("<code>", result)
    Assert.DoesNotContain("<b>", result)

// ─────────────────────────────────────────────────────────────────────────────
// Blockquote edge cases
// ─────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``standalone > with no text is HTML-escaped but not converted to blockquote`` () =
    // After HTML-escape ">" → "&gt;" (4 chars).
    // line.StartsWith("&gt; ") → false; length > 4 → false (4 = 4).
    // Falls through to `else line` → output is "&gt;".
    let result = markdownToHtml ">"
    Assert.Equal("&gt;", result)

[<Fact>]
let ``multi-line blockquote produces a blockquote tag for each line`` () =
    // Each "> " line is processed independently; the output has two blockquote tags.
    let input  = "> first\n> second"
    let result = markdownToHtml input
    let count  = result.Split("<blockquote>").Length - 1
    Assert.Equal(2, count)

[<Fact>]
let ``text after blockquote line passes through as normal`` () =
    // The non-"> " line is unchanged; the "> " line becomes a blockquote.
    let input  = "> quoted\nnormal"
    let result = markdownToHtml input
    Assert.Contains("<blockquote>quoted</blockquote>", result)
    Assert.Contains("normal", result)
    Assert.DoesNotContain("<blockquote>normal", result)
