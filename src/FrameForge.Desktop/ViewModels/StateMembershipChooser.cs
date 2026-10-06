using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FrameForge.Core.Models;

namespace FrameForge.Desktop.ViewModels;

/// <summary>One checkbox row of the state-membership chooser: All States, or one authored state.</summary>
public sealed partial class StateMembershipOption : ObservableObject
{
    private readonly StateMembershipChooser _owner;

    internal StateMembershipOption(StateMembershipChooser owner, string? id, string name, bool isChecked, bool isEnabled)
    {
        _owner = owner;
        Id = id;
        Name = name;
        _isChecked = isChecked;
        _isEnabled = isEnabled;
    }

    /// <summary>Null on the All States row; otherwise the authored design-state id.</summary>
    public string? Id { get; }

    /// <summary>The authored state name, or the literal "All States".</summary>
    public string Name { get; }

    public bool IsAllStates => Id is null;

    public override string ToString() => Name;

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isEnabled;

    partial void OnIsCheckedChanged(bool value) => _owner.NotifyOptionChanged(this);
}

/// <summary>
/// The "Choose States..." dialog model: All States plus every state the project currently has.
/// </summary>
/// <remarks>
/// <para>
/// The rows are built from the live <see cref="EditorMetadata.DesignStates"/> every time the
/// chooser is opened, so a newly created state appears immediately and a deleted one cannot be
/// offered. Nothing about a particular project's state names is known here.
/// </para>
/// <para>
/// All States and explicit membership are mutually exclusive. Checking All States clears and
/// disables the individual rows; checking an individual row leaves All States unchecked. The
/// model persists one or the other - an empty <c>StateIds</c> list IS All States - so the chooser
/// normalizes before Apply rather than letting an ambiguous pair reach the file.
/// </para>
/// <para>
/// Seeding follows the selection: when every selected object shares one membership it is offered
/// pre-checked, and when they disagree nothing is checked and <see cref="IsMixed"/> says so. Apply
/// replaces membership wholesale for every editable object in the selection; it never merges with
/// each object's old membership, because the user is stating what the set should be.
/// </para>
/// </remarks>
public sealed partial class StateMembershipChooser : ObservableObject
{
    private readonly List<StateMembershipOption> _rows = [];
    private bool _syncing;

    internal StateMembershipChooser(EditorMetadata editor, IReadOnlyList<string> targetNames)
    {
        TargetNames = [.. targetNames];
        EditableTargetNames = [.. targetNames.Where(name => !editor.IsLocked(name))];

        var memberships = targetNames
            .Select(name => (IReadOnlyList<string>)(editor.DesignObjectFor(name)?.StateIds ?? []))
            .ToList();
        IsMixed = memberships.Count > 1
                  && memberships.Any(ids => !SameMembership(ids, memberships[0]));

        var currentIds = editor.DesignStates.Select(state => state.Id).ToHashSet(StringComparer.Ordinal);
        string[] seed = IsMixed || memberships.Count == 0
            ? []
            : memberships[0].Where(currentIds.Contains).ToArray();
        var allStatesChecked = !IsMixed && seed.Length == 0;

        var allStates = new StateMembershipOption(this, null, "All States", allStatesChecked, isEnabled: true);
        _rows.Add(allStates);
        Options.Add(allStates);
        foreach (var state in editor.DesignStates)
        {
            var row = new StateMembershipOption(this, state.Id, state.Name,
                isChecked: !allStatesChecked && seed.Contains(state.Id, StringComparer.Ordinal),
                isEnabled: !allStatesChecked);
            _rows.Add(row);
            Options.Add(row);
        }
    }

    /// <summary>All States first, then the project's authored states in authored order.</summary>
    public ObservableCollection<StateMembershipOption> Options { get; } = [];

    /// <summary>The selected objects this chooser will be applied to.</summary>
    public IReadOnlyList<string> TargetNames { get; }

    /// <summary>The subset that Apply is allowed to rewrite. Locked objects are never here.</summary>
    public IReadOnlyList<string> EditableTargetNames { get; }

    public int LockedTargetCount => TargetNames.Count - EditableTargetNames.Count;

    /// <summary>True when the selected objects did not start from one shared membership.</summary>
    public bool IsMixed { get; }

    /// <summary>Explains what a mixed selection means, so Apply cannot read as a merge.</summary>
    public string MixedNote =>
        "These objects have different memberships. Apply replaces them with the choice below for every editable object.";

    /// <summary>True while the All States row is the one checked.</summary>
    public bool IsAllStates => _rows[0].IsChecked;

    /// <summary>Reports the locked part of the selection instead of silently skipping it later.</summary>
    public string LockNote => LockedTargetCount == 0
        ? string.Empty
        : $"{LockedTargetCount} locked object(s) will be left unchanged.";

    /// <summary>True when the choice is a legal membership and at least one object can change.</summary>
    public bool CanApply => EditableTargetNames.Count > 0 && (_rows[0].IsChecked || _rows.Skip(1).Any(row => row.IsChecked));

    /// <summary>Empty when Apply is legal; otherwise the one reason it is not.</summary>
    public string ValidationMessage => EditableTargetNames.Count == 0
        ? "Every selected object is locked. Unlock at least one before changing membership."
        : _rows[0].IsChecked || _rows.Skip(1).Any(row => row.IsChecked)
            ? string.Empty
            : "Choose All States or at least one design state.";

    /// <summary>
    /// Enforces All States / explicit exclusivity from whichever row just moved, so the pair that
    /// reaches Apply is always one legal membership.
    /// </summary>
    internal void NotifyOptionChanged(StateMembershipOption source)
    {
        if (_syncing)
            return;

        _syncing = true;
        if (source.IsAllStates)
        {
            if (source.IsChecked)
            {
                foreach (var row in _rows.Skip(1))
                {
                    row.IsChecked = false;
                    row.IsEnabled = false;
                }
            }
            else
            {
                foreach (var row in _rows.Skip(1))
                    row.IsEnabled = true;
            }
        }
        else if (source.IsChecked)
        {
            _rows[0].IsChecked = false;
            foreach (var row in _rows.Skip(1))
                row.IsEnabled = true;
        }
        _syncing = false;

        OnPropertyChanged(nameof(IsAllStates));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    /// <summary>
    /// The normalized decision: All States, or exactly the authored ids to persist. False when
    /// neither is chosen, which is the one combination a project cannot represent.
    /// </summary>
    public bool TryGetResult(out bool allStates, out IReadOnlyList<string> stateIds)
    {
        allStates = _rows[0].IsChecked;
        stateIds = allStates
            ? Array.Empty<string>()
            : _rows.Skip(1).Where(row => row.IsChecked).Select(row => row.Id!).ToArray();
        return allStates || stateIds.Count > 0;
    }

    /// <summary>Set equality over membership, because the stored order is not the meaning.</summary>
    public static bool SameMembership(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        Normalize(left).SequenceEqual(Normalize(right));

    /// <summary>The display line for one membership: "All States", or the authored state names.</summary>
    public static string Describe(EditorMetadata editor, IReadOnlyList<string> stateIds) => stateIds.Count == 0
        ? "All States"
        : string.Join(", ", stateIds.Select(id => editor.DesignStates.FirstOrDefault(state => state.Id == id)?.Name ?? id));

    private static string[] Normalize(IReadOnlyList<string> ids) =>
        [.. ids.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)];
}
