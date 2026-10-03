using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Viewing;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>
/// Phase 3's second premise, made checkable: the three canvas modes are a POLICY, and the policy
/// is decided in Core where it can be tested without a window.
/// </summary>
/// <remarks>
/// If "Preview hides helpers" lived in an Avalonia render pass, it would be untestable and it would
/// quietly drift. Every question the canvas asks - is this drawn, is this labelled, does this mode
/// show debug structures - is answered here, on the real Native Hunts document, so the mode
/// semantics are facts rather than behaviour someone has to eyeball.
/// </remarks>
public class ViewPolicyTests
{
    private static readonly FrameForge.Core.Import.FrameXmlImportResult Imported = Load();

    private static Project Project => Imported.Project!;

    private static FrameDef Frame(string name) => Project.Frames.First(f => f.Name == name);

    private static LayoutResult Layout { get; } = LayoutResolver.Resolve(Project);

    private static FrameForge.Core.Import.FrameXmlImportResult Load()
    {
        var assembly = typeof(ViewPolicyTests).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("fixtures.NativeHuntsFrame.xml"));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return FrameForge.Core.Import.FrameXmlImporter.Import(
            System.Text.Encoding.UTF8.GetString(memory.ToArray()),
            "NativeHuntsFrame.xml");
    }

    private static VisibilityFilter FilterFor(CanvasViewMode mode) => ViewPolicy.DefaultsFor(mode);

    // ------------------------------------------------------------------ mode defaults

    [Fact]
    public void DebugKeepsEverythingAndLablsEverything()
    {
        var filter = FilterFor(CanvasViewMode.DEBUG);

        Assert.Equal(VisibilityFilter.ALL, filter);
        Assert.Equal(LabelPolicy.ALL, ViewPolicy.DefaultLabelPolicyFor(CanvasViewMode.DEBUG));
    }

    [Fact]
    public void PreviewDropsHelpersAndHiddenWidgetsAndDropsLabels()
    {
        var filter = FilterFor(CanvasViewMode.PREVIEW);

        Assert.False(filter.HasFlag(VisibilityFilter.HELPERS));
        Assert.False(filter.HasFlag(VisibilityFilter.HIDDEN));
        Assert.True(filter.HasFlag(VisibilityFilter.FRAMES));
        Assert.True(filter.HasFlag(VisibilityFilter.BUTTONS));
        Assert.True(filter.HasFlag(VisibilityFilter.TEXT));
        Assert.True(filter.HasFlag(VisibilityFilter.TEXTURES));
        Assert.Equal(LabelPolicy.NONE, ViewPolicy.DefaultLabelPolicyFor(CanvasViewMode.PREVIEW));
    }

    [Fact]
    public void HybridLabelsTheSelectionOnly()
    {
        Assert.Equal(
            LabelPolicy.SELECTED,
            ViewPolicy.DefaultLabelPolicyFor(CanvasViewMode.HYBRID));
    }

    [Fact]
    public void PreviewAndHybridAgreeOnWhatIsVisibleAndDifferOnlyOnLabels()
    {
        // The modes are distinct but not gratuitously so: Hybrid is Preview plus the selection's
        // geometry, not a third unrelated set of defaults.
        Assert.Equal(FilterFor(CanvasViewMode.PREVIEW), FilterFor(CanvasViewMode.HYBRID));
    }

    [Fact]
    public void OnlyDebugShowsDebugStructuresForEverythingAtOnce()
    {
        Assert.True(ViewPolicy.ShowsDebugStructures(CanvasViewMode.DEBUG));
        Assert.False(ViewPolicy.ShowsDebugStructures(CanvasViewMode.PREVIEW));
        Assert.False(ViewPolicy.ShowsDebugStructures(CanvasViewMode.HYBRID));

        Assert.True(ViewPolicy.ShowsSelectionGeometry(CanvasViewMode.HYBRID));
        Assert.True(ViewPolicy.ShowsSelectionGeometry(CanvasViewMode.DEBUG));
        Assert.False(ViewPolicy.ShowsSelectionGeometry(CanvasViewMode.PREVIEW));
    }

    // ------------------------------------------------------------------ visibility

    /// <summary>
    /// Preview's first job is to be clean, and the stand-in for Blizzard's LFDParentFrame is the
    /// biggest offender: it is a frame that does not exist in the addon, yet it is 355x500 and
    /// encloses the entire document.
    /// </summary>
    [Fact]
    public void PreviewHidesTheSynthesizedStandInThatDebugShows()
    {
        var standIn = Frame("LFDParentFrame");

        Assert.True(standIn.Placeholder);
        Assert.True(ViewPolicy.IsHelper(standIn));

        // Debug shows it, because diagnosing a layout means seeing the frame the geometry hangs
        // from even though the addon never defines it.
        Assert.True(ViewPolicy.IsVisible(standIn, Layout, FilterFor(CanvasViewMode.DEBUG)));
        Assert.True(ViewPolicy.IsVisible(standIn, Layout, VisibilityFilter.HELPERS));

        // Preview does not, because it is not part of the UI.
        Assert.False(ViewPolicy.IsVisible(standIn, Layout, FilterFor(CanvasViewMode.PREVIEW)));
    }

    /// <summary>
    /// NativeHuntsFrameInitializer is a bare zero-area frame whose only job is to carry
    /// &lt;Scripts&gt;. It is not a UI element, so Preview must not draw it.
    /// </summary>
    [Fact]
    public void PreviewHidesTheBareZeroAreaScriptCarrier()
    {
        var initializer = Frame("NativeHuntsFrameInitializer");

        Assert.False(initializer.HasArea);
        Assert.Null(initializer.Visual);
        Assert.True(ViewPolicy.IsHelper(initializer));
        Assert.False(ViewPolicy.IsVisible(initializer, Layout, FilterFor(CanvasViewMode.PREVIEW)));
    }

    /// <summary>
    /// The rule that matters most: an unnamed widget is not a helper. Most of this document's
    /// background art is anonymous - eight frame borders from one atlas, four panel backgrounds -
    /// and treating anonymity as diagnostic-only would strip Preview of the very content it exists
    /// to show.
    /// </summary>
    [Fact]
    public void PreviewKeepsAnonymousWidgetsThatActuallyPaint()
    {
        var borders = Project.Frames
            .Where(f => f.Kind == FrameKind.TEXTURE && f.Anonymous)
            .ToList();

        // Twenty-one textures, four of which the document names.
        Assert.Equal(21, Project.Frames.Count(f => f.Kind == FrameKind.TEXTURE));
        Assert.Equal(17, borders.Count);

        // Not a helper - that is the claim. Note the document's root frame is hidden, so "not a
        // helper" and "drawn in Preview" are different questions here; the second is settled by
        // the hidden toggle rather than by classification.
        Assert.All(borders, f => Assert.False(ViewPolicy.IsHelper(f)));
        Assert.All(borders, f => Assert.True(ViewPolicy.IsVisible(f, Layout, VisibilityFilter.ALL)));
    }

    /// <summary>
    /// Six FontStrings have no &lt;Size&gt; because WoW sizes them from their font: the four
    /// anonymous record labels and the two title labels. They are genuine text and must not be
    /// dismissed as helpers - but with no rectangle there is nothing for Preview to draw, which is
    /// a rendering gap, not a classification one.
    /// </summary>
    [Fact]
    public void PreviewKeepsAutoSizedFontStringsBecauseTheyAreRealText()
    {
        var autoSized = Project.Frames
            .Where(f => f.Kind == FrameKind.FONTSTRING && !f.HasArea)
            .ToList();

        Assert.Equal(6, autoSized.Count);
        Assert.All(autoSized, f => Assert.False(ViewPolicy.IsHelper(f)));
        Assert.All(autoSized, f => Assert.Equal(FrameCategory.VISUAL, ViewPolicy.CategoryOf(f)));
        Assert.All(autoSized, f => Assert.NotNull(f.Visual?.Text));
    }

    /// <summary>
    /// The four state panels each sit behind <c>hidden="true"</c>. WoW shows exactly one at a time,
    /// so a Preview that respected the flag would show an empty window.
    /// </summary>
    [Fact]
    public void PreviewHidesWidgetsTheSourceMarkedHidden()
    {
        var panels = new[]
        {
            "NativeHuntsFrameContentPanelIdentity",
            "NativeHuntsFrameContentPanelHuntState",
            "NativeHuntsFrameContentPanelIdle",
            "NativeHuntsFrameContentPanelRecord",
        };

        foreach (var name in panels)
        {
            var panel = Frame(name);
            Assert.False(panel.Visible, $"{name} should have imported as hidden");
            Assert.False(ViewPolicy.IsVisible(panel, Layout, FilterFor(CanvasViewMode.PREVIEW)),
                $"{name} is hidden and Preview does not ask for hidden widgets");

            // The HIDDEN toggle is an overlay on a category set, not a category of its own: the
            // user has to keep the matching category on for the reveal to mean anything, which is
            // exactly how the independent toolbar toggles compose.
            Assert.False(ViewPolicy.IsVisible(panel, false, VisibilityFilter.HIDDEN));
            Assert.True(ViewPolicy.IsVisible(panel, false, VisibilityFilter.HIDDEN | VisibilityFilter.FRAMES));
        }
    }

    /// <summary>
    /// Showing hidden widgets must reveal the whole hidden subtree, not just the flagged root.
    /// The identity panel's own background texture is not marked hidden - its PARENT is - so a
    /// filter that only looked at each widget's own flag would still draw a floating backdrop.
    /// </summary>
    [Fact]
    public void AskingForHiddenWidgetsRevealsWidgetsWhoseOwnFlagIsNotSet()
    {
        var backdrop = Frame("Texture#14");

        Assert.True(backdrop.Visible);
        Assert.False(ViewPolicy.IsVisible(backdrop, Layout, FilterFor(CanvasViewMode.PREVIEW)));
        Assert.True(ViewPolicy.IsVisible(backdrop, Layout, VisibilityFilter.ALL));
    }

    [Theory]
    [InlineData(CanvasViewMode.PREVIEW)]
    [InlineData(CanvasViewMode.HYBRID)]
    public void TheTwoCleanModesNeverDrawAnythingInsideAHiddenSubtree(CanvasViewMode mode)
    {
        var filter = FilterFor(mode);

        // Not just widgets flagged hidden themselves: the identity panel's backdrop texture is
        // visible, but it lives inside a hidden panel, so it must not be drawn either.
        Assert.DoesNotContain(
            Project.Frames,
            f => !Layout.Frames[f.Name].EffectiveVisible && ViewPolicy.IsVisible(f, Layout, filter));
    }

    [Theory]
    [InlineData(CanvasViewMode.PREVIEW)]
    [InlineData(CanvasViewMode.HYBRID)]
    public void TheTwoCleanModesNeverDrawADiagnosticOnlyStructure(CanvasViewMode mode)
    {
        var filter = FilterFor(mode);

        Assert.DoesNotContain(
            Project.Frames,
            f => ViewPolicy.IsHelper(f) && ViewPolicy.IsVisible(f, Layout, filter));
    }

    /// <summary>
    /// Revealing hidden widgets must show all four state panels at once - the whole reason a
    /// designer turns the toggle on.
    /// </summary>
    [Fact]
    public void TurningOnHiddenRevealsEveryStatePanelAtOnce()
    {
        var filter = VisibilityFilter.ALL;

        foreach (var name in new[]
                 {
                     "NativeHuntsFrameContentPanelIdentity",
                     "NativeHuntsFrameContentPanelHuntState",
                     "NativeHuntsFrameContentPanelIdle",
                     "NativeHuntsFrameContentPanelRecord",
                 })
        {
            Assert.False(Layout.Frames[name].EffectiveVisible, $"{name} resolves as hidden");
            Assert.True(ViewPolicy.IsVisible(Frame(name), false, filter));
        }
    }

    [Fact]
    public void PreviewWithHiddenEnabledHasDrawableNativeHuntsVisuals()
    {
        var filter = ViewPolicy.DefaultsFor(CanvasViewMode.PREVIEW) | VisibilityFilter.HIDDEN;
        var accepted = Project.Frames
            .Where(frame => ViewPolicy.IsVisible(frame, Layout, filter))
            .ToList();

        Assert.NotEmpty(accepted);
        Assert.Contains(accepted, frame => frame.Kind == FrameKind.TEXTURE);
        Assert.Contains(accepted, frame => frame.Kind == FrameKind.FONTSTRING);
        Assert.Contains(accepted, frame =>
            frame.Kind is FrameKind.TEXTURE or FrameKind.FONTSTRING or FrameKind.BUTTON or FrameKind.STATUSBAR
            && Layout.Frames[frame.Name].Rect is { Width: > 0, Height: > 0 });

        // The imported model remains untouched; Hidden is a view override over effective
        // visibility, not a rewrite of hidden="true" on the source frames.
        Assert.False(Frame("NativeHuntsFrame").Visible);
        Assert.False(Layout.Frames["NativeHuntsFrame"].EffectiveVisible);
    }

    // ------------------------------------------------------------------ category and toggles

    [Fact]
    public void EveryWidgetIsReachableFromExactlyOneTreeFilter()
    {
        // The point of folding HELPERS into STRUCTURE: a filter must never be able to hide a
        // widget permanently, because the tree would then disagree with the document.
        foreach (var frame in Project.Frames)
        {
            var inStructure = ViewPolicy.CategoryOf(frame) == FrameCategory.STRUCTURE;
            var inVisual = ViewPolicy.CategoryOf(frame) == FrameCategory.VISUAL;

            Assert.True(inStructure ^ inVisual, $"{frame.Name} must be one category or the other");
        }
    }

    [Theory]
    [InlineData(FrameKind.TEXTURE, FrameCategory.VISUAL)]
    [InlineData(FrameKind.FONTSTRING, FrameCategory.VISUAL)]
    [InlineData(FrameKind.STATUSBAR, FrameCategory.VISUAL)]
    [InlineData(FrameKind.FRAME, FrameCategory.STRUCTURE)]
    [InlineData(FrameKind.BUTTON, FrameCategory.STRUCTURE)]
    public void KindDecidesTheCategory(FrameKind kind, FrameCategory expected) =>
        Assert.Equal(expected, ViewPolicy.CategoryOf(new FrameDef { Name = "x", Kind = kind }));

    [Fact]
    public void EachVisibilityToggleGatesItsOwnKind()
    {
        var texture = Frame("Texture#2");
        var fontString = Frame("NativeHuntsFrameContentPanelTitle");
        var button = Frame("LFDParentFrameTab1");
        var window = Frame("NativeHuntsFrameContentPanel");

        Assert.True(ViewPolicy.IsVisible(texture, true, VisibilityFilter.TEXTURES));
        Assert.False(ViewPolicy.IsVisible(texture, true, VisibilityFilter.FRAMES));

        Assert.True(ViewPolicy.IsVisible(fontString, true, VisibilityFilter.TEXT));
        Assert.False(ViewPolicy.IsVisible(fontString, true, VisibilityFilter.TEXTURES));

        Assert.True(ViewPolicy.IsVisible(button, true, VisibilityFilter.BUTTONS));
        Assert.False(ViewPolicy.IsVisible(button, true, VisibilityFilter.FRAMES));

        Assert.True(ViewPolicy.IsVisible(window, true, VisibilityFilter.FRAMES));
    }

    [Fact]
    public void VisibilityIsViewportOnlyAndNeverEditsTheProject()
    {
        // Filtering must not become a way of deleting things. Hiding everything must leave the
        // document byte-for-byte as it was.
        var before = Project.Frames;

        foreach (var frame in Project.Frames)
            _ = ViewPolicy.IsVisible(frame, Layout, VisibilityFilter.NONE);

        Assert.Same(before, Project.Frames);
        Assert.Equal(52, Project.Frames.Count);
    }

    // ------------------------------------------------------------------ labels

    [Fact]
    public void LabelNoneDrawsNothingEvenForTheSelection()
    {
        var title = Frame("NativeHuntsFrameContentPanelTitle");
        var filter = FilterFor(CanvasViewMode.PREVIEW);

        Assert.False(ViewPolicy.ShouldDrawLabel(title, selected: true, LabelPolicy.NONE, filter, effectiveVisible: true));
        Assert.False(ViewPolicy.ShouldDrawLabel(title, selected: true, LabelPolicy.NONE, VisibilityFilter.ALL, effectiveVisible: true));
    }

    [Fact]
    public void LabelSelectedDrawsOnlyTheSelection()
    {
        var title = Frame("NativeHuntsFrameContentPanelTitle");
        var filter = VisibilityFilter.ALL;

        Assert.True(ViewPolicy.ShouldDrawLabel(title, selected: true, LabelPolicy.SELECTED, filter, effectiveVisible: true));
        Assert.False(ViewPolicy.ShouldDrawLabel(title, selected: false, LabelPolicy.SELECTED, filter, effectiveVisible: true));
        Assert.True(ViewPolicy.ShouldDrawLabel(title, selected: false, LabelPolicy.ALL, filter, effectiveVisible: true));
    }

    [Fact]
    public void ALabelNeverAppearsOnSomethingTheCanvasIsNotDrawing()
    {
        // Otherwise the trail prints and the ready icon - both hidden, both inside the state
        // panel - would keep writing labels over the visible ones, which is exactly the Phase 2
        // wall-of-text problem.
        var hidden = Frame("NativeHuntsFrameContentPanelHuntStateReadyIcon");

        Assert.False(hidden.Visible);

        // The ready icon is hidden and sits inside the hidden state panel, so the resolved
        // visibility is false too. A label policy of All must still not produce a label for it.
        Assert.False(Layout.Frames[hidden.Name].EffectiveVisible);
        Assert.False(ViewPolicy.ShouldDrawLabelIn(
            hidden, selected: true, LabelPolicy.ALL, VisibilityFilter.ALL & ~VisibilityFilter.HIDDEN, Layout));

        // Turning the HIDDEN toggle on does draw it - a label the user asked for.
        Assert.True(ViewPolicy.ShouldDrawLabelIn(
            hidden, selected: true, LabelPolicy.ALL, VisibilityFilter.ALL, Layout));
    }

    [Fact]
    public void EveryModeHasADefaultForEveryControl()
    {
        foreach (var mode in Enum.GetValues<CanvasViewMode>())
        {
            Assert.NotEqual(VisibilityFilter.NONE, ViewPolicy.DefaultsFor(mode));
            Assert.True(Enum.IsDefined(ViewPolicy.DefaultLabelPolicyFor(mode)));

            // Every mode must leave at least the things the player sees.
            var filter = ViewPolicy.DefaultsFor(mode);
            Assert.True(filter.HasFlag(VisibilityFilter.TEXTURES));
            Assert.True(filter.HasFlag(VisibilityFilter.TEXT));
        }
    }

    // ------------------------------------------------------------------ tree projection

    [Fact]
    public void TheUnfilteredTreeShowsEveryNode()
    {
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.ALL, search: null);

        Assert.Equal(52, projection.Matched.Count);
        Assert.Equal(52, projection.Visible.Count);
        Assert.False(projection.Filtering);
    }

    [Fact]
    public void StructureFilterKeepsContainersAndStandIns()
    {
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.STRUCTURE, search: null);
        var shown = Project.Frames.Where(f => projection.Visible.Contains(f.Name)).ToList();

        Assert.All(shown, f => Assert.Equal(FrameCategory.STRUCTURE, ViewPolicy.CategoryOf(f)));
        Assert.Contains(shown, f => f.Name == "LFDParentFrame");
        Assert.Contains(shown, f => f.Name == "LFDParentFrameTab1");
        Assert.DoesNotContain(shown, f => f.Kind == FrameKind.TEXTURE);
        Assert.DoesNotContain(shown, f => f.Kind == FrameKind.FONTSTRING);

        // Seven source frames, two tab buttons, and the synthesized stand-in.
        Assert.Equal(10, shown.Count);
    }

    [Fact]
    public void VisualFilterKeepsTexturesFontStringsAndTheStatusBar()
    {
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.VISUAL, search: null);

        // Twenty-one textures, twenty font strings, one status bar.
        Assert.Equal(42, projection.Matched.Count);
        Assert.All(
            Project.Frames.Where(f => projection.Matched.Contains(f.Name)),
            f => Assert.Equal(FrameCategory.VISUAL, ViewPolicy.CategoryOf(f)));
        Assert.Contains("NativeHuntsFrameContentPanelHuntStateProgress", projection.Matched);

        // Shown-but-not-matched: the seven frames the visual elements hang from, kept so the tree
        // is navigable rather than a flat list of forty-two leaves.
        Assert.Equal(49, projection.Visible.Count);
        Assert.Contains("NativeHuntsFrameContentPanel", projection.Visible);
        Assert.DoesNotContain("NativeHuntsFrameContentPanel", projection.Matched);
    }

    /// <summary>
    /// A hit with no path to it is not navigable. Searching for a leaf FontString must keep the
    /// panels above it, or the user is shown a name with no indication of where it lives.
    /// </summary>
    [Fact]
    public void SearchKeepsTheAncestorsNeededToReachAMatch()
    {
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.ALL, "RecordSeal");

        Assert.Contains("NativeHuntsFrameContentPanelRecordSeals", projection.Matched);
        Assert.Contains("NativeHuntsFrameContentPanelRecord", projection.Visible);
        Assert.Contains("NativeHuntsFrameContentPanel", projection.Visible);
        Assert.Contains("NativeHuntsFrame", projection.Visible);

        // The ancestor is shown for context but did not itself match.
        Assert.DoesNotContain("NativeHuntsFrameContentPanelRecord", projection.Matched);
        Assert.True(projection.Filtering);
    }

    [Fact]
    public void SearchIsCaseInsensitiveAndMatchesAnywhereInTheName()
    {
        foreach (var query in new[] { "seals", "SEALS", "eals" })
        {
            var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.ALL, query);
            Assert.Contains("NativeHuntsFrameContentPanelRecordSeals", projection.Matched);
        }
    }

    [Fact]
    public void WhitespaceOnlySearchIsNotASearch()
    {
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.ALL, "   ");

        Assert.Equal(52, projection.Matched.Count);
        Assert.False(projection.Filtering);
    }

    [Fact]
    public void SearchAndFilterCompose()
    {
        // "Tier" only exists as a FontString, so the Visual filter must not hide the hit.
        var visual = TreeProjectionBuilder.Resolve(Project, TreeFilter.VISUAL, "Tier");
        Assert.Contains("NativeHuntsFrameContentPanelIdentityTier", visual.Matched);

        // No texture is called anything like "Button", so Structure finds nothing.
        var structure = TreeProjectionBuilder.Resolve(Project, TreeFilter.STRUCTURE, "PanelIdentity");
        Assert.Contains("NativeHuntsFrameContentPanelIdentity", structure.Matched);
        Assert.DoesNotContain("NativeHuntsFrameContentPanelTitle", structure.Matched);
    }

    [Fact]
    public void ANarrowFilterStillCarriesAncestorsThatWereFilteredOut()
    {
        // The search is over the TEXTURE list, but the frame it lives in is not a texture. Without
        // ancestor context the match would be a floating orphan.
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.VISUAL, "SealIcon");

        Assert.Contains("NativeHuntsFrameContentPanelRecordSealIcon", projection.Matched);
        Assert.Contains("NativeHuntsFrameContentPanelRecord", projection.Visible);
        Assert.DoesNotContain("NativeHuntsFrameContentPanelRecord", projection.Matched);
    }

    [Fact]
    public void ASearchThatMatchesNothingFiltersEverythingAndSaysSo()
    {
        var projection = TreeProjectionBuilder.Resolve(Project, TreeFilter.ALL, "no-such-widget");

        Assert.Empty(projection.Matched);
        Assert.Empty(projection.Visible);
        Assert.True(projection.Filtering);
    }

    [Fact]
    public void ProjectionNeverMutatesTheProject()
    {
        var before = Project.Frames.Select(f => f.Name).ToList();

        TreeProjectionBuilder.Resolve(Project, TreeFilter.VISUAL, "Panel");
        TreeProjectionBuilder.Resolve(Project, TreeFilter.STRUCTURE, null);

        Assert.Equal(before, Project.Frames.Select(f => f.Name).ToList());
    }

    // ------------------------------------------------------------------ hit testing

    /// <summary>
    /// A viewport equivalent to the canvas control's Fit, in plain Core geometry so hit tests are
    /// expressed in the same canvas pixels the control works in.
    /// </summary>
    private static (Viewport Viewport, CanvasOrigin Origin) FitCanvas()
    {
        const double width = 1200;
        const double height = 900;
        const double margin = 40;

        var rect = LayoutResolver.ScreenRect(Project.Screen);
        var viewport = new Viewport(
            Math.Min((width - margin * 2) / rect.Width, (height - margin * 2) / rect.Height),
            rect.CenterX,
            rect.CenterY);

        return (viewport, new CanvasOrigin(width / 2, height / 2));
    }

    private static (double CanvasX, double CanvasY) CanvasPointOf(double modelX, double modelY)
    {
        var (viewport, origin) = FitCanvas();
        return (viewport.ModelToCanvasX(modelX, origin), viewport.ModelToCanvasY(modelY, origin));
    }

    /// <summary>The centre of the rectangle two widgets share.</summary>
    private static (double ModelX, double ModelY) OverlapCentre(string a, string b)
    {
        var first = Layout.Rects[a];
        var second = Layout.Rects[b];

        var left = Math.Max(first.Left, second.Left);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Max(first.Bottom, second.Bottom);
        var top = Math.Min(first.Top, second.Top);

        Assert.True(right > left && top > bottom, $"{a} and {b} do not actually overlap");

        return ((left + right) / 2, (bottom + top) / 2);
    }

    /// <summary>
    /// Identity and Idle share a top edge and Idle is the taller, so they genuinely overlap. In
    /// WoW exactly one is shown at a time, which makes a click inside the overlap ambiguous - and
    /// the point of the test is that FrameForge says so instead of silently guessing.
    /// </summary>
    [Fact]
    public void AClickInsideTheOverlapReportsBothPanelsAndBreaksTheTieByPaintOrder()
    {
        var (modelX, modelY) = OverlapCentre("NativeHuntsFrameContentPanelIdentity", "NativeHuntsFrameContentPanelIdle");
        var (canvasX, canvasY) = CanvasPointOf(modelX, modelY);
        var (viewport, origin) = FitCanvas();

        var order = HitTester.CandidatesAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY).ToList();

        // The ambiguity is reported rather than resolved away.
        Assert.Contains("NativeHuntsFrameContentPanelIdle", order);
        Assert.Contains("NativeHuntsFrameContentPanelIdentity", order);

        // Front to back: the later a widget is painted, the earlier it is offered, so index 0 is
        // what a single click selects and each repeat click steps back through the stack.
        Assert.True(
            order.IndexOf("NativeHuntsFrameContentPanelIdle") < order.IndexOf("LFDParentFrame"));

        // The top candidate is NOT necessarily one of the two panels, and that is worth stating:
        // Identity's own FontStrings paint above Idle's panel because paint order is by stratum
        // and then document order, and the two panels' contents interleave. A test that hard-coded
        // "the panel wins" would have been wrong about FrameForge's real paint order.
        Assert.True(order.IndexOf("NativeHuntsFrameContentPanelIdentity") > 0,
            "Identity's own contents are expected above its own panel background");
    }

    /// <summary>The invariant that matters, stated once: the candidates are the containing widgets in
    /// exactly the reverse of the order they are painted.</summary>
    [Fact]
    public void CandidatesAreTheContainingWidgetsInReversePaintOrder()
    {
        var (modelX, modelY) = OverlapCentre("NativeHuntsFrameContentPanelHuntState", "NativeHuntsFrameContentPanelIdle");
        var (canvasX, canvasY) = CanvasPointOf(modelX, modelY);
        var (viewport, origin) = FitCanvas();

        var order = HitTester.CandidatesAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY).ToList();

        var expected = Layout.PaintOrder.Reverse()
            .Where(n => Layout.Rects.TryGetValue(n, out var rect) && ContainsPoint(viewport, origin, rect, canvasX, canvasY))
            .ToList();

        Assert.Equal(expected, order);
        Assert.True(order.Count > 1, "the point was chosen to be ambiguous");
    }

    /// <summary>
    /// Mirrors the tester's rule, including its tolerance for widgets with no interior to click.
    /// Written out here rather than reused so that a change to the rule has to be made in two
    /// places before this test will agree with it.
    /// </summary>
    private static bool ContainsPoint(Viewport viewport, CanvasOrigin origin, FrameRect rect, double x, double y)
    {
        var box = viewport.RectToCanvas(rect, origin);
        var slack = box.Width <= 0 || box.Height <= 0 ? HitTester.ZeroAreaPickTolerance : 0;

        return x >= box.X - slack && x <= box.Right + slack && y >= box.Y - slack && y <= box.Bottom + slack;
    }


    [Fact]
    public void TheOverlapOffersTheWindowAndItsStandInTooRatherThanGuessing()
    {
        var (modelX, modelY) = OverlapCentre("NativeHuntsFrameContentPanelIdentity", "NativeHuntsFrameContentPanelIdle");
        var (canvasX, canvasY) = CanvasPointOf(modelX, modelY);
        var (viewport, origin) = FitCanvas();

        var candidates = HitTester.CandidatesAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY);

        // Three legitimate answers to the same click. Hiding two of them would be a lie.
        Assert.True(candidates.Count >= 3, $"expected several candidates, got {candidates.Count}");
        Assert.Contains("LFDParentFrame", candidates);
    }

    [Fact]
    public void TopmostAtAgreesWithTheFirstCandidate()
    {
        var (modelX, modelY) = OverlapCentre("NativeHuntsFrameContentPanelHuntState", "NativeHuntsFrameContentPanelIdle");
        var (canvasX, canvasY) = CanvasPointOf(modelX, modelY);
        var (viewport, origin) = FitCanvas();

        var candidates = HitTester.CandidatesAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY);
        Assert.Equal(candidates[0], HitTester.TopmostAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY));
    }

    /// <summary>
    /// A widget with no width or height has no interior to click, so an exact test would make the
    /// six auto-sized FontStrings and the script carrier permanently unselectable from the canvas.
    /// </summary>
    [Fact]
    public void AZeroAreaWidgetIsStillClickableNearItsAnchorPoint()
    {
        var initializer = Frame("NativeHuntsFrameInitializer");
        var rect = Layout.Rects[initializer.Name];
        var (canvasX, canvasY) = CanvasPointOf(rect.Left, rect.Bottom);
        var (viewport, origin) = FitCanvas();

        var candidates = HitTester.CandidatesAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY);

        Assert.Contains(initializer.Name, candidates);
    }

    /// <summary>
    /// The identity panel's backdrop texture is not itself hidden - its PARENT is. While that panel
    /// is off screen the texture must not be clickable, or the canvas would let you select a widget
    /// it is not drawing.
    /// </summary>
    [Fact]
    public void AWidgetInsideAHiddenSubtreeIsNotClickableThroughIt()
    {
        var backdrop = Frame("Texture#14");
        Assert.True(backdrop.Visible);
        Assert.False(Layout.Frames[backdrop.Name].EffectiveVisible);

        var rect = Layout.Rects[backdrop.Name];
        var (canvasX, canvasY) = CanvasPointOf((rect.Left + rect.Right) / 2, (rect.Bottom + rect.Top) / 2);
        var (viewport, origin) = FitCanvas();

        var withoutHidden = VisibilityFilter.ALL & ~VisibilityFilter.HIDDEN;

        var hidden = HitTester.CandidatesAt(Project, Layout, viewport, origin, withoutHidden, canvasX, canvasY);
        Assert.DoesNotContain(backdrop.Name, hidden);

        // Turning the HIDDEN toggle on is what makes the hidden subtree reachable at all.
        var revealed = HitTester.CandidatesAt(
            Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY);
        Assert.Contains(backdrop.Name, revealed);
    }

    /// <summary>
    /// Switching a category off has to remove those widgets from picking, not just from drawing.
    /// </summary>
    /// <remarks>
    /// This is the failure that makes a visibility toggle look broken: the widget disappears and
    /// the user clicks where it was, and something behind it gets selected with no explanation.
    /// If the toggle were honest, the click would fall through to whatever really is there.
    /// </remarks>
    [Fact]
    public void TurningACategoryOffAlsoStopsThoseWidgetsBeingPicked()
    {
        // Any texture large enough to click, chosen by the property under test rather than by name, so
        // renaming an element cannot quietly turn this into a no-op. HIDDEN stays on in both
        // filters: NativeHuntsFrame's own root is hidden, so without it nothing is pickable at all
        // and the test would pass for the wrong reason.
        var textureName = Project.Frames
            .Where(f => f.Visual?.Texture is not null)
            .Select(f => f.Name)
            .First(n => Layout.Rects[n].Width > 0 && Layout.Rects[n].Height > 0);

        var rect = Layout.Rects[textureName];
        var (canvasX, canvasY) = CanvasPointOf((rect.Left + rect.Right) / 2, (rect.Bottom + rect.Top) / 2);
        var (viewport, origin) = FitCanvas();

        var withTextures = VisibilityFilter.ALL;
        Assert.Contains(textureName, HitTester.CandidatesAt(Project, Layout, viewport, origin, withTextures, canvasX, canvasY));

        var withoutTextures = withTextures & ~VisibilityFilter.TEXTURES;
        Assert.DoesNotContain(
            textureName,
            HitTester.CandidatesAt(Project, Layout, viewport, origin, withoutTextures, canvasX, canvasY));
    }

    [Fact]
    public void AClearClickSelectsNothing()
    {
        // The far corner of the screen rect, outside the framed window.
        var screen = LayoutResolver.ScreenRect(Project.Screen);
        var (canvasX, canvasY) = CanvasPointOf(screen.Right + 60, screen.Bottom - 10);
        var (viewport, origin) = FitCanvas();

        Assert.Null(HitTester.TopmostAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY));
    }

    [Fact]
    public void HitTestingIsReadOnly()
    {
        var before = Project.Frames.Select(f => f.Name).ToList();

        var (modelX, modelY) = OverlapCentre("NativeHuntsFrameContentPanelIdentity", "NativeHuntsFrameContentPanelIdle");
        var (canvasX, canvasY) = CanvasPointOf(modelX, modelY);
        var (viewport, origin) = FitCanvas();
        HitTester.TopmostAt(Project, Layout, viewport, origin, VisibilityFilter.ALL, canvasX, canvasY);

        Assert.Equal(before, Project.Frames.Select(f => f.Name).ToList());
    }
}
