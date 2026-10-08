using FrameForge.Core.Models;

namespace FrameForge.Desktop.Preview;

public enum PreviewButtonState { Normal, Selected }

/// <summary>One non-destructive, design-time contribution to an imported frame.</summary>
public sealed record PreviewFrameOverride(
    string FrameName,
    bool? Visible = null,
    string? Text = null,
    bool HasText = false,
    bool TextIsSample = false,
    ColorRgba? TextColor = null,
    string? TexturePath = null,
    bool HasTexture = false,
    double? StatusBarValue = null,
    PreviewButtonState? ButtonState = null,
    FrameAnchor? PrimaryAnchor = null,
    string? BehaviorSource = null);

public sealed record PreviewStateDefinition(
    string Id,
    string Label,
    string Description,
    IReadOnlyDictionary<string, PreviewFrameOverride> Overrides)
{
    public bool IsXmlDefaults => Id == PreviewStateRegistry.XmlDefaultsId;
}

public sealed record PreviewStateDiagnostic(string Code, string Message, string? FrameName = null);

public sealed record PreviewOverrideSet(
    PreviewStateDefinition State,
    IReadOnlyDictionary<string, PreviewFrameOverride> Overrides,
    IReadOnlyList<PreviewStateDiagnostic> Diagnostics)
{
    public PreviewFrameOverride? Find(string? frameName) =>
        frameName is not null && Overrides.TryGetValue(frameName, out var value) ? value : null;

    public IReadOnlyList<string> Describe(FrameDef frame)
    {
        if (Find(frame.Name) is not { } value)
            return [];
        var lines = new List<string> { $"preview override: {State.Label} (design-time only)" };
        if (value.Visible is { } visible)
            lines.Add($"effective visibility {(visible ? "shown" : "hidden")} (source {(frame.Visible ? "shown" : "hidden")})");
        if (value.HasText)
            lines.Add($"effective text \"{value.Text ?? string.Empty}\" ({(value.TextIsSample ? "sample content" : "runtime behavior text")}; source {(frame.Visual?.Text?.Text is null ? "runtime/unset" : "literal")})");
        if (value.HasTexture)
            lines.Add($"effective texture {value.TexturePath ?? "cleared"} (runtime-selected in source)");
        if (value.StatusBarValue is { } status)
            lines.Add($"effective status value {status:0.##} (source {frame.Visual?.StatusBar?.DefaultValue?.ToString("0.##") ?? "unset"})");
        if (value.ButtonState is { } button)
            lines.Add($"effective button state {button} (PanelTemplates_SetTab behavior)");
        if (value.PrimaryAnchor is { } anchor)
            lines.Add($"effective anchor {anchor.Point} -> {anchor.RelativePoint} {anchor.OffsetX:0.##},{anchor.OffsetY:0.##} (Lua Place behavior)");
        if (value.TextColor is { } color)
            lines.Add($"effective preview text color {color.R:0.##},{color.G:0.##},{color.B:0.##}");
        return lines;
    }
}

public interface IPreviewStateRegistry
{
    IReadOnlyList<PreviewStateDefinition> StatesFor(Project project);
    PreviewStateDefinition DefaultStateFor(Project project);
    PreviewOverrideSet Resolve(Project project, string? stateId);
    Project Apply(Project source, PreviewOverrideSet overrides);
}

/// <summary>
/// Generic preview-state engine. Catalogs supply data; this class performs no Native Hunts-specific
/// rendering and never mutates the source project.
/// </summary>
public sealed class PreviewStateRegistry : IPreviewStateRegistry
{
    public const string XmlDefaultsId = "xml-defaults";
    public static PreviewStateDefinition XmlDefaults { get; } = new(
        XmlDefaultsId, "XML Defaults", "No design-time runtime values; render the imported XML exactly.",
        new Dictionary<string, PreviewFrameOverride>(StringComparer.Ordinal));

    public IReadOnlyList<PreviewStateDefinition> StatesFor(Project project) =>
        NativeHuntsPreviewStates.IsApplicable(project)
            ? [XmlDefaults, .. NativeHuntsPreviewStates.Definitions]
            : [XmlDefaults];

    /// <summary>
    /// Functional imports open in one representative, explicitly simulated state. Other projects
    /// remain literal XML by default. The state is presentation-only and never mutates source data.
    /// </summary>
    public PreviewStateDefinition DefaultStateFor(Project project) =>
        NativeHuntsPreviewStates.IsApplicable(project)
            ? NativeHuntsPreviewStates.Definitions.First(state => state.Id == "idle")
            : XmlDefaults;

    public PreviewOverrideSet Resolve(Project project, string? stateId)
    {
        var states = StatesFor(project);
        var state = states.FirstOrDefault(item => item.Id == stateId);
        if (state is not null)
            return Validate(project, state, []);
        return Validate(project, XmlDefaults,
            string.IsNullOrWhiteSpace(stateId)
                ? []
                : [new PreviewStateDiagnostic("unknown-preview-state",
                    $"Preview state '{stateId}' does not apply to this project; XML Defaults are active.")]);
    }

    public Project Apply(Project source, PreviewOverrideSet overrides)
    {
        if (overrides.Overrides.Count == 0)
            return source;
        var changed = false;
        var frames = source.Frames.Select(frame =>
        {
            if (!overrides.Overrides.TryGetValue(frame.Name, out var value))
                return frame;
            changed = true;
            var visual = frame.Visual;
            if (value.HasText)
            {
                var text = visual?.Text ?? new TextVisual(null);
                visual = (visual ?? new FrameVisual()) with { Text = text with { Text = value.Text } };
            }
            if (value.HasTexture)
            {
                var texture = visual?.Texture ?? new TextureVisual(null, TexCoords.Full);
                visual = (visual ?? new FrameVisual()) with { Texture = texture with { File = value.TexturePath } };
            }
            if (value.StatusBarValue is { } status)
            {
                var bar = visual?.StatusBar ?? new StatusBarVisual();
                visual = (visual ?? new FrameVisual()) with { StatusBar = bar with { DefaultValue = status } };
            }
            var anchor = value.PrimaryAnchor;
            return frame with
            {
                Visible = value.Visible ?? frame.Visible,
                Visual = visual,
                Point = anchor?.Point ?? frame.Point,
                RelativeTo = anchor?.RelativeTo ?? frame.RelativeTo,
                RelativePoint = anchor?.RelativePoint ?? frame.RelativePoint,
                OffsetX = anchor?.OffsetX ?? frame.OffsetX,
                OffsetY = anchor?.OffsetY ?? frame.OffsetY,
                ExtraAnchors = anchor is null ? frame.ExtraAnchors : [],
            };
        }).ToArray();
        return changed ? source with { Frames = frames } : source;
    }

    private static PreviewOverrideSet Validate(Project project, PreviewStateDefinition state,
        IReadOnlyList<PreviewStateDiagnostic> initial)
    {
        var diagnostics = new List<PreviewStateDiagnostic>(initial);
        var valid = new Dictionary<string, PreviewFrameOverride>(StringComparer.Ordinal);
        foreach (var item in state.Overrides)
        {
            if (!project.Contains(item.Key))
            {
                diagnostics.Add(new PreviewStateDiagnostic("missing-preview-target",
                    $"Preview state '{state.Label}' targets missing frame '{item.Key}'.", item.Key));
                continue;
            }
            valid[item.Key] = item.Value;
        }
        return new PreviewOverrideSet(state, valid, diagnostics);
    }
}

/// <summary>Evidence-backed Native Hunts state behavior plus clearly labeled sample content.</summary>
public static class NativeHuntsPreviewStates
{
    private const string Root = "NativeHuntsFrame";
    private const string Identity = "NativeHuntsFrameContentPanelIdentity";
    private const string Icon = Identity + "Icon";
    private const string Tier = Identity + "Tier";
    private const string Prey = Identity + "Prey";
    private const string Issuer = Identity + "Issuer";
    private const string HuntState = "NativeHuntsFrameContentPanelHuntState";
    private const string Header = HuntState + "Header";
    private const string Primary = HuntState + "Primary";
    private const string Secondary = HuntState + "Secondary";
    private const string Progress = HuntState + "Progress";
    private const string ProgressText = HuntState + "ProgressText";
    private const string Decoration = HuntState + "Decoration";
    private const string ReadyIcon = HuntState + "ReadyIcon";
    private const string Idle = "NativeHuntsFrameContentPanelIdle";
    private const string IdleState = Idle + "State";
    private const string IdleDescription = Idle + "Description";
    private const string Record = "NativeHuntsFrameContentPanelRecord";
    private const string RecordStandard = Record + "Standard";
    private const string RecordElite = Record + "Elite";
    private const string RecordAvailability = Record + "Availability";
    private const string RecordSealIcon = Record + "SealIcon";
    private const string RecordSeals = Record + "Seals";
    private const string DungeonTab = "LFDParentFrameTab1";
    private const string HuntsTab = "LFDParentFrameTab2";

    private static readonly ColorRgba AvailableGreen = new(0x20 / 255d, 1, 0x20 / 255d);
    private static readonly ColorRgba QuestGold = new(1, 0.82, 0);

    public static IReadOnlyList<PreviewStateDefinition> Definitions { get; } =
    [
        BuildIdle(),
        BuildActive("standard-hunt", "Standard Hunt", "STANDARD HUNT", "Ashfang",
            @"Interface\NativeHunts\hunt_icon_standard.tga", "Durotar", 60),
        BuildActive("elite-hunt", "Elite Hunt", "ELITE HUNT", "The Oathbreaker",
            @"Interface\NativeHunts\hunt_icon_elite.tga", "The Barrens", 65),
        BuildComplete(),
    ];

    public static bool IsApplicable(Project project) =>
        project.Contains(Root) && project.Contains(Icon) && project.Contains(HuntsTab);

    private static PreviewStateDefinition BuildIdle()
    {
        var values = Common();
        Visible(values, Identity, false);
        Visible(values, HuntState, false);
        Visible(values, Idle, true);
        Visible(values, Progress, false);
        Visible(values, Decoration, false);
        Visible(values, ReadyIcon, false);
        Text(values, IdleState, "NO ACTIVE HUNT", sample: false);
        Text(values, IdleDescription, "Speak with a Huntmaster\nto begin a Hunt.", sample: false);
        return Definition("idle", "Idle", "Real no-active-hunt presentation with representative record values.", values);
    }

    private static PreviewStateDefinition BuildActive(string id, string label, string tier, string prey,
        string icon, string zone, double progress)
    {
        var values = Common();
        Visible(values, Identity, true);
        Visible(values, HuntState, true);
        Visible(values, Idle, false);
        Visible(values, Progress, true);
        Visible(values, Decoration, true);
        Visible(values, ReadyIcon, false);
        Texture(values, Icon, icon);
        Text(values, Tier, tier, sample: false);
        Text(values, Prey, prey, sample: true);
        Text(values, Issuer, "Huntmaster Gorrak  •  Orgrimmar", sample: true);
        Text(values, Header, "HUNT PROGRESS", sample: false);
        Text(values, Primary, "Tracking", sample: false);
        Text(values, Secondary, $"Follow the trail through {zone}.", sample: true);
        Text(values, ProgressText, $"{progress:0}%", sample: true);
        values[Progress] = values[Progress] with { StatusBarValue = progress };
        Anchor(values, Primary, 15, -34);
        Anchor(values, Secondary, 15, -78);
        return Definition(id, label, $"Real tracking-state behavior with clearly labeled {label.ToLowerInvariant()} sample content.", values);
    }

    private static PreviewStateDefinition BuildComplete()
    {
        var values = Common();
        Visible(values, Identity, true);
        Visible(values, HuntState, true);
        Visible(values, Idle, false);
        Visible(values, Progress, false);
        Visible(values, Decoration, false);
        Visible(values, ReadyIcon, true);
        Texture(values, Icon, @"Interface\NativeHunts\hunt_icon_standard.tga");
        Text(values, Tier, "STANDARD HUNT", sample: false);
        Text(values, Prey, "Ashfang", sample: true);
        Text(values, Issuer, "Huntmaster Gorrak  •  Orgrimmar", sample: true);
        Text(values, Header, "HUNT COMPLETE", sample: false);
        Text(values, Primary, "READY TO TURN IN", sample: false, color: QuestGold);
        Text(values, Secondary, "Return to Huntmaster Gorrak\nin Orgrimmar.", sample: true);
        Text(values, ProgressText, string.Empty, sample: false);
        values[Progress] = values[Progress] with { StatusBarValue = 0 };
        Anchor(values, Primary, 15, -43);
        Anchor(values, Secondary, 15, -75);
        return Definition("hunt-complete", "Hunt Complete", "Real ready-to-turn-in presentation with representative identity data.", values);
    }

    private static Dictionary<string, PreviewFrameOverride> Common()
    {
        var values = new Dictionary<string, PreviewFrameOverride>(StringComparer.Ordinal);
        Visible(values, Root, true);
        Visible(values, Record, true);
        Visible(values, RecordSealIcon, true);
        Text(values, RecordStandard, "12", sample: true);
        Text(values, RecordElite, "2", sample: true);
        Text(values, RecordAvailability, "Available", sample: true, color: AvailableGreen);
        Text(values, RecordSeals, "3", sample: true);
        values[DungeonTab] = New(DungeonTab) with { ButtonState = PreviewButtonState.Normal };
        values[HuntsTab] = New(HuntsTab) with { ButtonState = PreviewButtonState.Selected };
        return values;
    }

    private static PreviewStateDefinition Definition(string id, string label, string description,
        Dictionary<string, PreviewFrameOverride> values) => new(id, label, description, values);

    private static PreviewFrameOverride New(string name) => new(name,
        BehaviorSource: "NativeHuntsFrame.lua (inspected, never executed)");

    private static PreviewFrameOverride Prior(IDictionary<string, PreviewFrameOverride> values, string name) =>
        values.TryGetValue(name, out var value) ? value : New(name);

    private static void Visible(IDictionary<string, PreviewFrameOverride> values, string name, bool visible) =>
        values[name] = Prior(values, name) with { Visible = visible };

    private static void Text(IDictionary<string, PreviewFrameOverride> values, string name, string text,
        bool sample, ColorRgba? color = null) =>
        values[name] = Prior(values, name) with
            { Text = text, HasText = true, TextIsSample = sample, TextColor = color };

    private static void Texture(IDictionary<string, PreviewFrameOverride> values, string name, string path) =>
        values[name] = Prior(values, name) with { TexturePath = path, HasTexture = true };

    private static void Anchor(IDictionary<string, PreviewFrameOverride> values, string name, double x, double y) =>
        values[name] = Prior(values, name) with
            { PrimaryAnchor = FrameAnchor.Create(AnchorPoint.TOPLEFT, HuntState, AnchorPoint.TOPLEFT, x, y) };
}
