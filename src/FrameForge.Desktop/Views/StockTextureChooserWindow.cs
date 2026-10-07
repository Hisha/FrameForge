using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FrameForge.Desktop.Assets;

namespace FrameForge.Desktop.Views;

/// <summary>
/// Compact logical-asset chooser for a StatusBar fill texture from stock WoW art.
/// </summary>
/// <remarks>
/// The user picks a portable, archive-relative <c>Interface/...</c> reference - the catalog plus
/// every Interface path the project or the extracted cache already knows, or a typed reference that
/// passes <see cref="WoWClientAssetProvider.TryNormalizeInterfacePath"/>. The window never touches
/// the project and never returns a machine path; resolution happens later against the configured
/// WoW client through the normal asset pipeline.
/// </remarks>
public sealed class StockTextureChooserWindow : Window
{
    private readonly ListBox _listBox;
    private readonly TextBox _filterBox;
    private readonly TextBox _customBox;
    private readonly TextBlock _validation;

    /// <summary>The chosen logical reference, or null when the dialog was cancelled.</summary>
    public string? SelectedInterfacePath { get; private set; }

    public StockTextureChooserWindow(
        IReadOnlyList<StockTextureEntry> entries,
        string? currentValue,
        string clientStatus,
        bool hasWowClientConfiguration)
    {
        Title = "Choose a WoW client texture";
        Width = 560;
        Height = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        List<StockTextureEntry> filtered = [.. entries];

        _customBox = new TextBox
        {
            Text = currentValue ?? string.Empty,
            PlaceholderText = "Interface/TargetingFrame/UI-StatusBar",
        };

        _listBox = new ListBox
        {
            DisplayMemberBinding = null,
            ItemsSource = filtered,
        };
        _listBox.Classes.Set("listbox", true);
        _listBox.SelectionChanged += (_, _) =>
        {
            if (_listBox.SelectedItem is StockTextureEntry entry)
                _customBox.Text = entry.InterfacePath;
        };

        _filterBox = new TextBox { PlaceholderText = "Filter Interface paths…" };
        _filterBox.TextChanged += (_, _) =>
        {
            var needle = _filterBox.Text?.Trim() ?? string.Empty;
            var matches = needle.Length == 0
                ? entries
                : entries.Where(entry => entry.InterfacePath.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToArray();
            filtered.Clear();
            foreach (var entry in matches)
                filtered.Add(entry);
        };

        _validation = new TextBlock { Classes = { "validation" }, TextWrapping = TextWrapping.Wrap };

        var clientLine = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            Text = hasWowClientConfiguration
                ? $"WoW client: {clientStatus}"
                : "No WoW client is configured. The reference is saved and resolves once a build-12340 client is configured.",
        };

        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var okay = new Button { Content = "Choose", MinWidth = 88, Classes = { "accent" } };
        cancel.Click += (_, _) => Close();
        okay.Click += (_, _) => Submit();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, okay },
        };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Logical Interface references from a WoW 3.3.5a (build-12340) client. The reference is saved, never the machine path, and nothing is copied into the project.",
                },
                clientLine,
                _filterBox,
                _listBox,
                new TextBlock { Text = "Or type a reference:" },
                _customBox,
                _validation,
                buttons,
            },
        };

        Grid.SetRow(_listBox, 3);

        Dispatcher.UIThread.Post(() => _customBox.Focus(), DispatcherPriority.Loaded);
    }

    private void Submit()
    {
        var candidate = _customBox.Text?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            _validation.Text = "Choose a texture from the list or type an Interface reference.";
            return;
        }
        if (!WoWClientAssetProvider.TryNormalizeInterfacePath(candidate, out _, out var error))
        {
            _validation.Text = error;
            return;
        }
        SelectedInterfacePath = candidate.Replace('\\', '/');
        Close();
    }
}