using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core.Geometry;
using FrameForge.Core.Semantics.V2;

namespace FrameForge.Desktop.ViewModels;

public sealed record V2GroupOption(SemanticId Id, string Name, int Count, bool Locked)
{
    public override string ToString() => $"{(Locked ? "🔒 " : string.Empty)}{Name} ({Count})";
}

public sealed partial class MainWindowViewModel
{
    private double _v2GestureRawX;
    private double _v2GestureRawY;
    private double _v2GestureAppliedX;
    private double _v2GestureAppliedY;
    private double _v2GestureAnchorX;
    private double _v2GestureAnchorY;
    private bool _v2KeyboardNudgeActive;
    private string? _v2GestureKind;

    [ObservableProperty] private bool _v2SnapEnabled;
    [ObservableProperty] private bool _v2GridVisible;
    [ObservableProperty] private double _v2GridSize = 8;
    [ObservableProperty] private string _v2GroupNameDraft = "Group";
    [ObservableProperty] private V2GroupOption? _selectedV2Group;

    public ObservableCollection<V2GroupOption> V2Groups { get; } = [];
    public bool CanAlignV2 => V2Selection.OrderedIds.Count(id => V2Document?.Nodes.Any(node => node.Id == id) == true) >= 2;
    public bool CanDistributeV2 => V2Selection.OrderedIds.Count(id => V2Document?.Nodes.Any(node => node.Id == id) == true) >= 3;
    public bool CanResizeV2 => V2Selection.Count == 1 && SelectedV2Node?.Kind is
        UiNodeKind.Frame or UiNodeKind.Texture or UiNodeKind.Button or UiNodeKind.StatusBar;

    partial void OnV2GridSizeChanged(double value)
    {
        if (!double.IsFinite(value) || value < 1) V2GridSize = 1;
        else if (value > 256) V2GridSize = 256;
    }

    public void ArrangeV2Selection(SelectionArrangeCommand command)
    {
        if (_v2Session is null || V2Selection.PrimaryId is not { } primary) return;
        ExecuteV2(SelectionArrange.Label(command),
            (editor, document) => editor.ArrangeSelection(document, V2Selection.OrderedIds, primary, command),
            $"{SelectionArrange.Label(command)} applied relative to the primary selection.");
    }

    public void ReorderV2Selection(int destination)
    {
        if (SelectedV2Node is not { } node) return;
        var label = destination switch
        {
            int.MinValue => "Send to back",
            int.MaxValue => "Bring to front",
            < 0 => "Send backward",
            _ => "Bring forward",
        };
        ExecuteV2(label, (editor, document) => editor.ReorderControl(document, node.Id, destination),
            $"{label}: changed native order within {node.Owner.Kind}; strata, level and draw layer were not changed.");
    }

    public void ApplyV2DragDelta(SemanticId id, double rawDeltaX, double rawDeltaY)
    {
        if (_v2Session is null || !IsV2DesignMode || !V2Selection.OrderedIds.Contains(id)) return;
        if (!_v2Session.InTransaction)
        {
            _v2Session.BeginTransaction(V2Selection.Count > 1 ? "Move selection" : "Drag control");
            StartV2Gesture("move", id);
        }
        _v2GestureRawX += rawDeltaX;
        _v2GestureRawY += rawDeltaY;
        var desiredX = SnapGesture(_v2GestureAnchorX, _v2GestureRawX);
        var desiredY = SnapGesture(_v2GestureAnchorY, _v2GestureRawY);
        var deltaX = desiredX - _v2GestureAppliedX;
        var deltaY = desiredY - _v2GestureAppliedY;
        if (Math.Abs(deltaX) < 1e-9 && Math.Abs(deltaY) < 1e-9) return;
        var change = _v2Session.Execute("Move selection delta",
            (editor, document) => editor.MoveSelection(document, V2Selection.OrderedIds, deltaX, deltaY));
        if (!change.Success)
        {
            _v2Session.CancelTransaction();
            ResetV2Gesture();
        }
        else
        {
            _v2GestureAppliedX = desiredX;
            _v2GestureAppliedY = desiredY;
        }
        ApplyV2Change(change, V2Selection.Count > 1 ? $"Moving {V2Selection.Count} controls." : "Moving control.");
    }

    public void ApplyV2ResizeDelta(SemanticId id, V2ResizeHandle handle, double rawDeltaX, double rawDeltaY)
    {
        if (_v2Session is null || !IsV2DesignMode || V2Selection.Count != 1 || V2Selection.PrimaryId != id) return;
        if (!_v2Session.InTransaction)
        {
            _v2Session.BeginTransaction("Resize control");
            StartV2Gesture("resize", id);
        }
        _v2GestureRawX += rawDeltaX;
        _v2GestureRawY += rawDeltaY;
        var desiredX = V2SnapEnabled ? Math.Round(_v2GestureRawX / V2GridSize) * V2GridSize : _v2GestureRawX;
        var desiredY = V2SnapEnabled ? Math.Round(_v2GestureRawY / V2GridSize) * V2GridSize : _v2GestureRawY;
        var deltaX = desiredX - _v2GestureAppliedX;
        var deltaY = desiredY - _v2GestureAppliedY;
        if (Math.Abs(deltaX) < 1e-9 && Math.Abs(deltaY) < 1e-9) return;
        var change = _v2Session.Execute("Resize delta",
            (editor, document) => editor.ResizeBy(document, id, handle, deltaX, deltaY));
        if (!change.Success)
        {
            _v2Session.CancelTransaction();
            ResetV2Gesture();
        }
        else
        {
            _v2GestureAppliedX = desiredX;
            _v2GestureAppliedY = desiredY;
        }
        ApplyV2Change(change, "Resizing control with authored dimensions.");
    }

    public void CompleteV2Gesture()
    {
        if (_v2Session?.InTransaction != true) return;
        var kind = _v2GestureKind ?? "visual edit";
        ApplyV2Change(_v2Session.CommitTransaction(), $"Completed {kind} gesture as one undo entry.");
        ResetV2Gesture();
    }

    public void CancelV2Gesture()
    {
        if (_v2Session?.InTransaction != true) return;
        ApplyV2Change(_v2Session.CancelTransaction(), "Cancelled gesture; document and history are unchanged.");
        ResetV2Gesture();
    }

    public void NudgeV2Selection(double deltaX, double deltaY)
    {
        if (_v2Session is null || !IsV2DesignMode || V2Selection.Count == 0) return;
        if (!_v2KeyboardNudgeActive)
        {
            _v2Session.BeginTransaction("Nudge selection");
            _v2KeyboardNudgeActive = true;
        }
        var change = _v2Session.Execute("Nudge delta",
            (editor, document) => editor.MoveSelection(document, V2Selection.OrderedIds, deltaX, deltaY));
        if (!change.Success)
        {
            _v2Session.CancelTransaction();
            _v2KeyboardNudgeActive = false;
        }
        ApplyV2Change(change, $"Nudged {V2Selection.Count} selected control(s).");
    }

    public void CompleteV2Nudge()
    {
        if (!_v2KeyboardNudgeActive || _v2Session?.InTransaction != true) return;
        ApplyV2Change(_v2Session.CommitTransaction(), "Nudged selection as one keyboard gesture.");
        _v2KeyboardNudgeActive = false;
    }

    public void CreateV2Group()
    {
        if (_v2Session is null) return;
        ExecuteV2("Create editor group", (editor, document) =>
                editor.CreateGroup(document, V2GroupNameDraft, V2Selection.OrderedIds),
            $"Created editor-only group '{V2GroupNameDraft}'. Native ownership is unchanged.");
    }

    public void SelectV2Group()
    {
        if (_v2Session is null || SelectedV2Group is not { } option ||
            V2Document?.Editor?.Groups.FirstOrDefault(group => group.Id == option.Id) is not { } group) return;
        _v2Session.ReplaceSelection(null);
        foreach (var member in group.Members) _v2Session.SelectPrimary(member);
        RefreshV2Presentation($"Selected {group.Members.Count} member(s) of '{group.Name}'.");
        RefreshV2Inspector();
    }

    public void ToggleSelectedV2GroupLock()
    {
        if (SelectedV2Group is not { } option) return;
        ExecuteV2(option.Locked ? "Unlock editor group" : "Lock editor group",
            (editor, document) => editor.UpdateGroup(document, option.Id, locked: !option.Locked),
            $"{(option.Locked ? "Unlocked" : "Locked")} editor-only group '{option.Name}'.");
    }

    public void RenameSelectedV2Group()
    {
        if (SelectedV2Group is not { } option) return;
        ExecuteV2("Rename editor group",
            (editor, document) => editor.UpdateGroup(document, option.Id, name: V2GroupNameDraft),
            $"Renamed editor-only group to '{V2GroupNameDraft}'.");
    }

    public void ReplaceSelectedV2GroupMembers()
    {
        if (SelectedV2Group is not { } option) return;
        ExecuteV2("Update editor group membership",
            (editor, document) => editor.UpdateGroup(document, option.Id, members: V2Selection.OrderedIds),
            $"Updated editor-only membership for '{option.Name}'. Native ownership is unchanged.");
    }

    public void DeleteSelectedV2Group()
    {
        if (SelectedV2Group is not { } option) return;
        ExecuteV2("Delete editor group", (editor, document) => editor.DeleteGroup(document, option.Id),
            $"Deleted editor-only group '{option.Name}'; native elements were preserved.");
    }

    private void StartV2Gesture(string kind, SemanticId id)
    {
        ResetV2Gesture();
        _v2GestureKind = kind;
        var anchor = V2Document?.Nodes.FirstOrDefault(node => node.Id == id)?.Anchors.FirstOrDefault();
        _v2GestureAnchorX = anchor?.OffsetX ?? 0;
        _v2GestureAnchorY = anchor?.OffsetY ?? 0;
    }

    private double SnapGesture(double start, double rawDelta) => V2SnapEnabled
        ? Math.Round((start + rawDelta) / V2GridSize) * V2GridSize - start
        : rawDelta;

    private void ResetV2Gesture()
    {
        _v2GestureRawX = _v2GestureRawY = _v2GestureAppliedX = _v2GestureAppliedY = 0;
        _v2GestureAnchorX = _v2GestureAnchorY = 0;
        _v2GestureKind = null;
    }

    private void RefreshV2Groups()
    {
        var selectedId = SelectedV2Group?.Id;
        V2Groups.Clear();
        foreach (var group in V2Document?.Editor?.Groups ?? [])
            V2Groups.Add(new V2GroupOption(group.Id, group.Name, group.Members.Count, group.Locked));
        SelectedV2Group = V2Groups.FirstOrDefault(group => group.Id == selectedId) ?? V2Groups.FirstOrDefault();
        OnPropertyChanged(nameof(CanAlignV2));
        OnPropertyChanged(nameof(CanDistributeV2));
        OnPropertyChanged(nameof(CanResizeV2));
    }
}
