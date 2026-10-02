using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core;
using FrameForge.Core.Examples;
using FrameForge.Core.Geometry;
using FrameForge.Core.Models;
using FrameForge.Core.Serialization;

namespace FrameForge.Desktop.ViewModels;

/// <summary>
/// The editor's state and commands.
/// </summary>
/// <remarks>
/// The view model owns the <see cref="Project"/>, re-resolves the layout through
/// <see cref="LayoutResolver"/> after every mutation, and pushes the result at the canvas.
/// It never performs geometry maths of its own: absolute bounds, paint order and visibility
/// inheritance all come from FrameForge.Core so there is exactly one implementation.
/// <para>
/// File dialogs are NOT handled here. <see cref="MainWindow"/> owns the window and performs
/// the pickers, then calls the Open/Save methods on this class with a path.
/// </para>
/// </remarks>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private Project _project = ProjectFactory.Empty();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _projectPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(SaveHint))]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = "Ready.";

    [ObservableProperty]
    private string _selectionSummary = "No frame selected.";

    [ObservableProperty]
    private LayoutResult _layout = LayoutResolver.Resolve(ProjectFactory.Empty());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFrame))]
    [NotifyPropertyChangedFor(nameof(CanEditSelection))]
    private string? _selectedName;

    [ObservableProperty]
    private FrameTreeNode? _selectedTreeNode;

    public MainWindowViewModel()
    {
        Editor = new FrameEditorViewModel((n, u, r) => ReplaceFrame(n, u, r), BuildFrameOptions, AnchorOptions);
        RelaidOut(_project, null, "New project. Load the Native Hunts example from the toolbar.");
    }

    /// <summary>The inspector bound panel for the selected frame.</summary>
    public FrameEditorViewModel Editor { get; }

    /// <summary>Tree contents, rebuilt from the parent relationship.</summary>
    public ObservableCollection<FrameTreeNode> TreeRoots { get; } = [];

    /// <summary>Flat, paint-ordered frame names, used to bind the tree selection.</summary>
    public ObservableCollection<string> CanvasSelectionNames { get; } = [];

    /// <summary>Projects available from the New menu.</summary>
    public IReadOnlyList<string> RecentProjectNames { get; } = [];

    /// <summary>The project currently open.</summary>
    public Project Project
    {
        get => _project;
        private set => SetProperty(ref _project, value);
    }

    /// <summary>True when the Native Hunts example is loaded, used to label the status hint.</summary>
    public string ProjectDescription =>
        Project.Frames.Count == 0
            ? "Empty project."
            : $"{Project.Frames.Count} frames on a {Number(Project.Screen.Width)} x {Number(Project.Screen.Height)} screen.";

    /// <summary>Title bar text, including the unsaved marker.</summary>
    public string WindowTitle
    {
        get
        {
            var name = string.IsNullOrEmpty(ProjectPath)
                ? Project.Name
                : System.IO.Path.GetFileName(ProjectPath);
            return $"{name}{(IsDirty ? " *" : string.Empty)} - FrameForge";
        }
    }

    /// <summary>Hint under the toolbar buttons.</summary>
    public string SaveHint => IsDirty ? "Unsaved changes" : "Saved";

    /// <summary>The selected frame, or null.</summary>
    public FrameDef? SelectedFrame => Project.Find(SelectedName);

    /// <summary>True when the inspector can be edited.</summary>
    public bool CanEditSelection => SelectedFrame is not null;

    /// <summary>Replaces the whole project. Used by New / Open / Load Example.</summary>
    public void Load(Project project, string? path, string status)
    {
        Project = project;
        ProjectPath = path ?? string.Empty;
        IsDirty = false;

        var keep = project.Frames.FirstOrDefault(f => f.Name == SelectedName)?.Name;
        SelectedName = keep;
        RelaidOut(project, keep, status);
    }

    /// <summary>Creates a fresh project.</summary>
    public void NewProject() => Load(ProjectFactory.Empty(), null, "New project created.");

    /// <summary>Loads the built-in Native Hunts example.</summary>
    public void LoadNativeHuntsExample() =>
        Load(NativeHuntsExample.CreateProject(), null,
            "Loaded the Native Hunts example. Identity, State and Idle overlap on purpose.");

    /// <summary>Loads a project from disk and reports any error in the status bar.</summary>
    public void OpenFromFile(string path)
    {
        try
        {
            var result = ProjectCodec.Parse(File.ReadAllText(path));
            if (!result.Ok)
            {
                RelaidOut(Project, SelectedName, $"Could not open {FileName(path)}: {result.ErrorText}");
                return;
            }

            Load(result.Project!, path, $"Opened {FileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RelaidOut(Project, SelectedName, $"Could not open {FileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Writes the project to disk. Returns false when the write failed.</summary>
    public bool SaveToFile(string path)
    {
        try
        {
            File.WriteAllText(path, ProjectCodec.Serialize(Project));
            ProjectPath = path;
            IsDirty = false;
            Status = $"Saved {FileName(path)}.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not save {FileName(path)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Selects a frame, or clears the selection with null.</summary>
    public void Select(string? name)
    {
        if (name is not null && !Project.Contains(name))
            name = null;

        if (SelectedName == name && FindNode(name) == SelectedTreeNode)
            return;

        SelectedName = name;
        RelaidOut(Project, name, null);
    }

    /// <summary>Keeps the tree selection in step when the user clicks the tree.</summary>
    partial void OnSelectedTreeNodeChanged(FrameTreeNode? value) => Select(value?.Name);

    private FrameTreeNode? FindNode(string? name)
    {
        if (name is null)
            return null;

        foreach (var root in TreeRoots)
        {
            var found = FindNode(root, name);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static FrameTreeNode? FindNode(FrameTreeNode node, string name) =>
        node.Name == name ? node : node.Children.Select(child => FindNode(child, name)).FirstOrDefault(found => found is not null);

    /// <summary>
    /// Applies a drag to the selected frame's offsets.
    /// </summary>
    /// <remarks>
    /// The delta arrives in MODEL units from the canvas, so dragging down the screen arrives
    /// as a negative Y and correctly decreases <c>offsetY</c>.
    /// </remarks>
    public void DragFrame(string name, double modelDx, double modelDy)
    {
        var frame = Project.Find(name);
        if (frame is null)
            return;

        var updated = frame with { OffsetX = frame.OffsetX + modelDx, OffsetY = frame.OffsetY + modelDy };
        ReplaceFrame(name, updated);
        IsDirty = true;
    }

    /// <summary>Called when a drag gesture ends.</summary>
    public void EndDrag()
    {
        if (IsDirty)
            Status = $"Dragged {SelectedName}.";
    }

    /// <summary>Adds a frame under the current selection and selects it.</summary>
    public void AddFrame()
    {
        var parent = SelectedName;
        var name = UniqueName(parent is null ? "Frame" : $"{parent}Child");
        var frame = new FrameDef
        {
            Name = name,
            Parent = parent,
            Width = 100,
            Height = 40,
            Point = AnchorPoint.TOPLEFT,
            RelativePoint = AnchorPoint.TOPLEFT,
        };

        Project = Project with { Frames = [.. Project.Frames, frame] };
        IsDirty = true;
        SelectedName = name;
        RelaidOut(Project, name, $"Added frame \"{name}\".");
    }

    /// <summary>
    /// Removes the selected frame.
    /// </summary>
    /// <remarks>
    /// Descendants are RE-PARENTED to the removed frame's parent, not deleted: deleting a
    /// window should not silently destroy the panels inside it. Frames that used the removed
    /// frame as an anchor target fall back to inheriting their own parent.
    /// </remarks>
    public void DeleteFrame()
    {
        if (SelectedName is not { } name || Project.Find(name) is not { } removed)
            return;

        var descendants = FrameHierarchy.Subtree(Project, name).Skip(1).ToHashSet(StringComparer.Ordinal);
        var frames = Project.Frames
            .Where(f => f.Name != name)
            .Select(f => f with
            {
                Parent = f.Parent == name ? removed.Parent : f.Parent,
                RelativeTo = f.RelativeTo == name ? null : f.RelativeTo,
            })
            .ToArray();

        var parent = removed.Parent ?? "the root";
        Project = Project with { Frames = frames };
        IsDirty = true;
        SelectedName = parent;
        RelaidOut(Project, SelectedName, descendants.Count == 0
            ? $"Deleted \"{name}\"."
            : $"Deleted \"{name}\"; re-parented {descendants.Count} descendant(s) to {parent}.");
    }

    /// <summary>Canvas hit-test entry point; also used to keep the tree in sync.</summary>
    public void OnCanvasSelectionRequested(string? name) => Select(name);

    /// <summary>Fits the layout to the canvas viewport.</summary>
    public void RequestFit() => Status = "Fitted the layout to the canvas.";

    private void ReplaceFrame(string name, FrameDef updated, string? rename = null)
    {
        var targetName = rename ?? updated.Name;

        var frames = Project.Frames.Select(f => f.Name == name
                ? updated with { Name = targetName }
                : f with
                {
                    Parent = f.Parent == name ? targetName : f.Parent,
                    RelativeTo = f.RelativeTo == name ? targetName : f.RelativeTo,
                }).ToArray();
        if (frames.Select(f => f with { }).SequenceEqual(Project.Frames.Select(f => f with { })))
            return;

        Project = Project with { Frames = frames };
        IsDirty = true;
        RelaidOut(Project, name, null);
    }

    private void RelaidOut(Project project, string? selection, string? status)
    {
        Layout = LayoutResolver.Resolve(project);
        RebuildTree(project);
        SelectedTreeNode = FindNode(selection);
        Editor.Refresh(project, selection);
        Editor.RefreshResolved(selection is null ? null : Layout.Frames.GetValueOrDefault(selection));
        CanvasSelectionNames.Clear();
        foreach (var name in Layout.PaintOrder)
            CanvasSelectionNames.Add(name);

        OnPropertyChanged(nameof(ProjectDescription));
        SelectionSummary = Editor.ResolvedSummary;

        if (status is not null)
            Status = status;
        else if (Editor.HasIssues)
            Status = $"Issue: {Editor.IssueText}";
        else if (selection is not null)
            Status = $"{selection}: {Editor.ResolvedSummary}";
        else
            Status = project.IssueSummary();
    }

    private void RebuildTree(Project project)
    {
        TreeRoots.Clear();
        foreach (var root in FrameHierarchy.Children(project, null))
            TreeRoots.Add(BuildNode(project, root));
    }

    private static FrameTreeNode BuildNode(Project project, FrameDef frame) =>
        new(frame, FrameHierarchy.Children(project, frame.Name).Select(child => BuildNode(project, child)).ToArray());

    private IReadOnlyList<FrameOption> BuildFrameOptions()
    {
        var options = new List<FrameOption> { new(null) };
        foreach (var frame in Project.Frames)
        {
            // A frame cannot be parented under itself or one of its own descendants, and must
            // not anchor to them either, or the engine would report a cycle.
            if (SelectedName is { } name && FrameHierarchy.IsDescendantOf(Project, frame.Name, name))
                continue;

            options.Add(new FrameOption(frame.Name));
        }

        return options;
    }

    private IReadOnlyList<AnchorPoint> AnchorOptions() => AnchorPoints.All;

    private string UniqueName(string prefix)
    {
        var candidate = prefix;
        var index = 1;
        while (Project.Contains(candidate))
            candidate = $"{prefix}{++index}";

        return candidate;
    }

    private static string Number(double value) =>
        value == Math.Floor(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FileName(string path)
    {
        try
        {
            return System.IO.Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>Layout diagnostics rendered into the status bar.</summary>
public static class ProjectDiagnostics
{
    /// <summary>A one-line summary of unresolved frames, or a confirmation when clean.</summary>
    public static string IssueSummary(this Project project)
    {
        var layout = LayoutResolver.Resolve(project);
        return layout.Issues.Count == 0
            ? $"Layout is clean: {layout.Rects.Count} of {project.Frames.Count} frames resolved."
            : $"{layout.Issues.Count} layout issue(s): {string.Join("; ", layout.Issues.Take(3).Select(i => i.ToString()))}";
    }
}