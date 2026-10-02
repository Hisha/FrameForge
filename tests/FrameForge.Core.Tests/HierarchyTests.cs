using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using static FrameForge.Core.Tests.TestProject;
using Xunit;

namespace FrameForge.Core.Tests;

/// <summary>Parent/child navigation, cycle safety, and draw order.</summary>
public class HierarchyTests
{
    private static readonly Project Example = NativeHuntsExample.CreateProject();

    [Fact]
    public void FindsRootFrames()
    {
        Assert.Equal(["MainWindow"], FrameHierarchy.Children(Example, null).Select(f => f.Name));
    }

    [Fact]
    public void FindsDirectChildren()
    {
        Assert.Equal(
            ["Identity", "State", "Idle", "Record"],
            FrameHierarchy.Children(Example, "Content").Select(f => f.Name));
    }

    [Fact]
    public void WalksASubtreeDepthFirst()
    {
        Assert.Equal(
            ["Content", "Identity", "State", "Idle", "Record"],
            FrameHierarchy.Subtree(Example, "Content"));
    }

    [Fact]
    public void ListsAncestorsFromNearestToFurthest()
    {
        Assert.Equal(["Content", "MainWindow"], FrameHierarchy.Ancestors(Example, "Idle"));
        Assert.Empty(FrameHierarchy.Ancestors(Example, "MainWindow"));
    }

    [Fact]
    public void DetectsDescendants()
    {
        Assert.True(FrameHierarchy.IsDescendantOf(Example, "Idle", "Content"));
        Assert.False(FrameHierarchy.IsDescendantOf(Example, "Content", "Idle"));
        Assert.True(FrameHierarchy.IsDescendantOf(Example, "Content", "Content"));
    }

    /// <summary>
    /// State and Idle are ANCHORED to Identity but PARENTED to Content. That distinction is
    /// exactly what the re-parenting guard has to get right.
    /// </summary>
    [Fact]
    public void OffersTheFullForbiddenSetForReparenting()
    {
        Assert.Equal(
            ["Content", "Identity", "State", "Idle", "Record"],
            FrameHierarchy.SelfAndDescendants(Example, "Content"));
        Assert.Equal(["Identity"], FrameHierarchy.SelfAndDescendants(Example, "Identity"));
    }

    [Fact]
    public void RefusesToReparentAFrameUnderItsOwnDescendant()
    {
        // Record is a child of Content, so Content under Record closes the loop.
        Assert.True(FrameHierarchy.WouldCreateCycle(Example, "Content", "Record"));
        // Idle is *anchored* to Identity but *parented* to Content, so Identity under Idle is
        // not a parent cycle. The guard is about the parent tree only; a resulting anchor loop
        // is reported separately as a layout diagnostic.
        Assert.False(FrameHierarchy.WouldCreateCycle(Example, "Identity", "Idle"));
        Assert.True(FrameHierarchy.WouldCreateCycle(Example, "Identity", "Identity"));

        Assert.False(FrameHierarchy.WouldCreateCycle(Example, "Identity", "Content"));
        Assert.False(FrameHierarchy.WouldCreateCycle(Example, "Identity", null));
    }

    [Fact]
    public void TerminatesOnAParentCycle()
    {
        var cyclic = Project(Frame("A", parent: "B"), Frame("B", parent: "A"));

        Assert.Equal(["A", "B"], FrameHierarchy.Subtree(cyclic, "A").Order());
        Assert.Equal(["B"], FrameHierarchy.Ancestors(cyclic, "A"));
        Assert.Equal(1, FrameHierarchy.Depths(cyclic)["A"]);
    }

    [Fact]
    public void ComputesDepthSoParentsPaintBehindChildren()
    {
        var depths = FrameHierarchy.Depths(Example);

        Assert.Equal(0, depths["MainWindow"]);
        Assert.Equal(1, depths["Content"]);
        Assert.Equal(2, depths["Identity"]);
    }

    [Fact]
    public void OrdersByDepthSoParentsSitBehindChildren()
    {
        Assert.Equal(
            ["MainWindow", "Content", "Identity", "State", "Idle", "Record"],
            LayoutResolver.ComputePaintOrder(Example));
    }

    [Fact]
    public void HonoursStratumBeforeAuthoredOrder()
    {
        var layered = Project(
            Frame("Front", stratum: Stratum.TOOLTIP),
            Frame("Back", stratum: Stratum.BACKGROUND));

        Assert.Equal(["Back", "Front"], LayoutResolver.ComputePaintOrder(layered));
    }

    [Fact]
    public void HonoursLevelWithinAStratum()
    {
        var layered = Project(
            Frame("Second", level: 2),
            Frame("First", level: 1));

        Assert.Equal(["First", "Second"], LayoutResolver.ComputePaintOrder(layered));
    }
}