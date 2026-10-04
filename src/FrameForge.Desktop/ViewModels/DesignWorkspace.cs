using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.ViewModels;

public enum WorkspaceExperience { Design, Inspect }

public sealed partial class WorkspaceOption : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    internal WorkspaceOption(MainWindowViewModel owner, WorkspaceExperience workspace)
        => (_owner, Workspace) = (owner, workspace);
    public WorkspaceExperience Workspace { get; }
    public string Label => Workspace == WorkspaceExperience.Design ? "DESIGN" : "INSPECT";
    public bool IsSelected
    {
        get => _owner.Workspace == Workspace;
        set { if (value) _owner.SetWorkspace(Workspace); }
    }
    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}

public sealed record DesignStateChoice(string? Id, string Name)
{
    public bool IsAllStates => Id is null;
    public override string ToString() => Name;
    public static DesignStateChoice All { get; } = new(null, "All States");
    public static DesignStateChoice From(DesignState state) => new(state.Id, state.Name);
}
