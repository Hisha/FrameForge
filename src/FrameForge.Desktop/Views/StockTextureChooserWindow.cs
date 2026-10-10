using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    private readonly ComboBox _directoryBox;
    private readonly TextBlock _validation;
    private readonly Image _preview;
    private readonly TextBlock _details;
    private readonly ITextureAssetResolver? _assets;
    private readonly bool _allowProjectAssets;
    private int _previewGeneration;
    private int _thumbnailLoads;
    private readonly SemaphoreSlim _thumbnailGate = new(1);

    /// <summary>The chosen logical reference, or null when the dialog was cancelled.</summary>
    public string? SelectedInterfacePath { get; private set; }

    public StockTextureChooserWindow(
        IReadOnlyList<StockTextureEntry> entries,
        string? currentValue,
        string clientStatus,
        bool hasWowClientConfiguration,
        ITextureAssetResolver? assets = null,
        string? catalogStatus = null,
        bool allowProjectAssets = false)
    {
        Title = "Choose a WoW client texture";
        Width = 820;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _assets = assets;
        _allowProjectAssets = allowProjectAssets;
        List<StockTextureEntry> filtered = [.. entries];
        _preview = new Image { Width = 260, Height = 260, Stretch = Stretch.Uniform };
        _details = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "mono" } };

        _customBox = new TextBox
        {
            Text = currentValue ?? string.Empty,
            PlaceholderText = "Interface/TargetingFrame/UI-StatusBar",
        };

        _listBox = new ListBox
        {
            DisplayMemberBinding = null,
            ItemsSource = filtered,
            Height = 360,
        };
        _listBox.ItemTemplate = new FuncDataTemplate<StockTextureEntry>((entry, nameScope) =>
        {
            var thumbnail = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
            var text = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = entry.InterfacePath, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = entry.Category, Opacity = 0.65, FontSize = 11 },
                },
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { thumbnail, text } };
            ToolTip.SetTip(row, entry.Notes);
            _ = LoadThumbnailAsync(entry.InterfacePath, thumbnail);
            return row;
        }, supportsRecycling: true);
        _listBox.Classes.Set("listbox", true);
        _listBox.SelectionChanged += async (_, _) =>
        {
            if (_listBox.SelectedItem is StockTextureEntry entry)
            {
                _customBox.Text = entry.InterfacePath;
                await PreviewAsync(entry.InterfacePath);
            }
        };

        _filterBox = new TextBox { PlaceholderText = "Search texture names and paths…" };
        var directories = entries.Select(entry =>
            {
                var path = entry.InterfacePath.Replace('\\', '/');
                var slash = path.LastIndexOf('/');
                return slash > 0 ? path[..slash] : "Project artwork";
            }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Prepend("All directories").ToArray();
        _directoryBox = new ComboBox { ItemsSource = directories, SelectedIndex = 0, PlaceholderText = "Directory" };
        void ApplyFilter()
        {
            var needle = _filterBox.Text?.Trim() ?? string.Empty;
            var directory = _directoryBox.SelectedItem as string;
            var matches = entries.Where(entry =>
                (needle.Length == 0 || entry.InterfacePath.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || entry.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)) &&
                (directory is null or "All directories" || entry.InterfacePath.Replace('\\', '/').StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
            filtered.Clear();
            foreach (var entry in matches)
                filtered.Add(entry);
            _listBox.ItemsSource = null;
            _listBox.ItemsSource = filtered;
        }
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        _directoryBox.SelectionChanged += (_, _) => ApplyFilter();

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

        var browser = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,12,2*") };
        var left = new StackPanel { Spacing = 8, Children = { _directoryBox, _filterBox, _listBox } };
        var right = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Selected texture preview", FontWeight = FontWeight.SemiBold },
                new Border { Background = new SolidColorBrush(Color.Parse("#10161C")), Padding = new Avalonia.Thickness(8), Child = _preview },
                _details,
            },
        };
        Grid.SetColumn(right, 2);
        browser.Children.Add(left);
        browser.Children.Add(right);

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
                new TextBlock { Text = catalogStatus ?? string.Empty, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
                browser,
                new TextBlock { Text = "Or type a reference:" },
                _customBox,
                _validation,
                buttons,
            },
        };

        Dispatcher.UIThread.Post(() => _customBox.Focus(), DispatcherPriority.Loaded);
    }

    private async Task PreviewAsync(string reference)
    {
        if (_assets is null) return;
        var generation = ++_previewGeneration;
        _details.Text = $"Loading {reference}…";
        await _thumbnailGate.WaitAsync();
        ResolvedTextureAsset asset;
        try
        {
            asset = await Task.Run(() => _assets.Resolve(reference));
        }
        finally
        {
            _thumbnailGate.Release();
        }
        if (generation != _previewGeneration) return;
        if (!asset.CanRender || asset.Texture is null)
        {
            _preview.Source = null;
            _details.Text = $"{reference}\n{asset.Status}: {asset.Diagnostic.Message}";
            return;
        }
        _preview.Source = asset.Texture.Bitmap;
        var alpha = asset.Texture.Image.Bgra.Where((_, index) => index % 4 == 3).Any(value => value < 255)
            ? "contains transparency" : "opaque";
        _details.Text = $"{reference}\n{asset.Width} × {asset.Height} · {asset.Format.ToString().ToUpperInvariant()} · {alpha}";
    }

    private async Task LoadThumbnailAsync(string reference, Image target)
    {
        if (_assets is null || Interlocked.Increment(ref _thumbnailLoads) > 64) return;
        await _thumbnailGate.WaitAsync();
        try
        {
            var asset = await Task.Run(() => _assets.Resolve(reference));
            if (asset.CanRender && asset.Texture is not null)
                target.Source = asset.Texture.Bitmap;
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    /// <summary>Automation hook used by the real-window acceptance smoke.</summary>
    public async Task SearchAndPreviewAsync(string text)
    {
        _filterBox.Text = text;
        var items = _listBox.ItemsSource?.Cast<StockTextureEntry>().ToArray() ?? [];
        var entry = items.FirstOrDefault(item =>
                        item.InterfacePath.EndsWith("UI-LFG-FRAME.blp", StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault(item =>
                        item.InterfacePath.Contains("LFGFrame", StringComparison.OrdinalIgnoreCase))
                    ?? items.FirstOrDefault();
        if (entry is not null)
        {
            _listBox.SelectedItem = entry;
            _customBox.Text = entry.InterfacePath;
            await PreviewAsync(entry.InterfacePath);
        }
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
            string? projectError = null;
            if (!_allowProjectAssets || !TextureAssetResolver.TryNormalizeProjectAsset(candidate, out candidate, out projectError))
            {
                _validation.Text = _allowProjectAssets ? projectError ?? error : error;
                return;
            }
        }
        SelectedInterfacePath = candidate.Replace('\\', '/');
        Close();
    }
}
