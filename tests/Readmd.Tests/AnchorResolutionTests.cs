using Readmd.Terminal;

namespace Readmd.Tests;

/// <summary>
/// Covers in-document "#fragment" link resolution for the terminal viewer: explicit HTML anchors
/// (<a id>/<a name>) must be navigable (they were previously dropped, giving "Anchor not found"),
/// and GitHub-style heading anchors must still resolve against Markdig's default heading ids.
/// </summary>
public class AnchorResolutionTests
{
    // ---- explicit HTML anchors (the dominant case: 128/147 refs in the reported document) ----

    [Fact]
    public void Explicit_html_anchor_block_is_registered_and_points_at_following_content()
    {
        var md = "<a id=\"ev-05-whoami\"></a>\n\n## 05 — whoami\n\nBody.\n";
        var anchors = Render.Anchors(md);

        Assert.True(anchors.ContainsKey("ev-05-whoami"));
        // The anchor resolves to the heading it precedes (at the heading's line, or the anchor's own
        // line immediately above it).
        int viaExplicit = AnchorResolver.Resolve(anchors, "ev-05-whoami");
        int viaHeading = AnchorResolver.Resolve(anchors, "whoami");
        Assert.True(viaExplicit >= 0);
        Assert.True(viaHeading >= viaExplicit && viaHeading - viaExplicit <= 1,
            $"explicit anchor line {viaExplicit} should be at or just above heading line {viaHeading}");
    }

    [Fact]
    public void Explicit_anchor_lookup_is_case_insensitive()
    {
        var anchors = Render.Anchors("<a id=\"Top\"></a>\n\n# Title\n");
        Assert.True(AnchorResolver.Resolve(anchors, "top") >= 0);
        Assert.True(AnchorResolver.Resolve(anchors, "TOP") >= 0);
    }

    [Fact]
    public void Anchor_name_attribute_is_registered()
    {
        var anchors = Render.Anchors("<a name=\"legacy\"></a>\n\n# Title\n");
        Assert.True(AnchorResolver.Resolve(anchors, "legacy") >= 0);
    }

    [Fact]
    public void Inline_html_anchor_in_paragraph_is_registered()
    {
        var md = "# Title\n\nSee <a id=\"marker\"></a> here for details.\n";
        var anchors = Render.Anchors(md);
        Assert.True(AnchorResolver.Resolve(anchors, "marker") >= 0);
    }

    [Fact]
    public void Data_and_aria_attributes_are_not_treated_as_anchors()
    {
        var md = "<div data-id=\"nope\" aria-labelledby=\"also-no\"></div>\n\n# Title\n";
        var anchors = Render.Anchors(md);
        Assert.False(anchors.ContainsKey("nope"));
        Assert.False(anchors.ContainsKey("also-no"));
    }

    [Fact]
    public void Unknown_anchor_returns_negative_one()
    {
        var anchors = Render.Anchors("# Only Heading\n");
        Assert.Equal(-1, AnchorResolver.Resolve(anchors, "does-not-exist"));
    }

    // ---- GitHub-style heading self-references (Bug B: both pipelines now emit GitHub ids) ----

    [Fact]
    public void Heading_id_is_registered_for_exact_self_reference()
    {
        var anchors = Render.Anchors("## Findings at a glance\n");
        Assert.True(AnchorResolver.Resolve(anchors, "findings-at-a-glance") >= 0);
    }

    [Theory]
    // GitHub keeps a leading number that Markdig's *default* auto-identifier would drop...
    [InlineData("## 5. How the server metadata was derived\n", "5-how-the-server-metadata-was-derived")]
    [InlineData("## 1. Findings at a glance\n", "1-findings-at-a-glance")]
    // ...and keeps the double dash from "A — B" that the default would collapse.
    [InlineData("## Appendix A — Embedded Evidence\n", "appendix-a--embedded-evidence")]
    [InlineData("## 6. Attacker synthesis — facts assembled\n", "6-attacker-synthesis--facts-assembled")]
    public void Github_style_heading_anchor_resolves(string md, string githubAnchor)
    {
        var anchors = Render.Anchors(md);
        Assert.True(AnchorResolver.Resolve(anchors, githubAnchor) >= 0,
            $"expected GitHub-style anchor '#{githubAnchor}' to resolve");
    }

    [Fact]
    public void Tolerant_match_resolves_anchor_that_omits_leading_number()
    {
        // Heading id is "1-introduction" (GitHub), but a hand-written link may drop the number.
        var anchors = Render.Anchors("## 1. Introduction\n");
        Assert.True(anchors.ContainsKey("1-introduction"));
        Assert.True(AnchorResolver.Resolve(anchors, "introduction") >= 0);
    }

    [Fact]
    public void Url_encoded_fragment_is_decoded_before_matching()
    {
        var anchors = Render.Anchors("<a id=\"a b\"></a>\n\n# Title\n");
        Assert.True(AnchorResolver.Resolve(anchors, "a%20b") >= 0);
    }

    // ---- normalization unit checks ----

    [Theory]
    [InlineData("1-findings-at-a-glance", "findings-at-a-glance")]
    [InlineData("findings-at-a-glance", "findings-at-a-glance")]
    [InlineData("appendix-a--embedded-evidence", "appendix-a-embedded-evidence")]
    [InlineData("a.employee-pii-high", "a-employee-pii-high")]
    [InlineData("4a-employee-pii-high", "a-employee-pii-high")]
    [InlineData("2024", "2024")]  // purely numeric: keep, don't strip to empty
    public void Normalize_bridges_slug_dialects(string input, string expected)
    {
        Assert.Equal(expected, AnchorResolver.Normalize(input));
    }

    [Fact]
    public void Exact_match_wins_over_tolerant_match()
    {
        // Two headings whose tolerant forms collide; the exact id must still win.
        var md = "## Intro\n\n## 1. Intro\n";
        var anchors = Render.Anchors(md);
        int exact = AnchorResolver.Resolve(anchors, "intro");
        Assert.Equal(anchors["intro"], exact);
    }
}
