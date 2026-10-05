using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using FrameForge.Core.Serialization;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.ViewModels;

namespace FrameForge.Desktop.Views;

/// <summary>
/// The editor window.
/// </summary>
/// <remarks>
/// Code-behind owns everything that needs a window: the native file pickers, the canvas
/// push/pull, and the smoke-test hooks. All editor state and commands live in
/// <see cref="MainWindowViewModel"/>.
/// </remarks>
public partial class MainWindow : Window
{
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The canvas is a plain (non-Avalonia-property) control, so it is refreshed explicitly
    /// whenever the view model resolves a new layout or changes the selection.
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (ViewModel is { } vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            SyncCanvas();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.Layout)
            or nameof(MainWindowViewModel.Project)
            or nameof(MainWindowViewModel.SelectedName)
            or nameof(MainWindowViewModel.ViewMode)
            or nameof(MainWindowViewModel.CanvasFilter)
            or nameof(MainWindowViewModel.LabelPolicy)
            or nameof(MainWindowViewModel.OriginFilter)
            or nameof(MainWindowViewModel.HiddenByOrigin)
            or nameof(MainWindowViewModel.LockedNames)
            or nameof(MainWindowViewModel.PreferredSelectionNames)
            or nameof(MainWindowViewModel.Assets))
        {
            SyncCanvas();
        }
    }

    /// <summary>Fits once the control has a real size, which is not known at construction.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SyncCanvas();
        Canvas.FitToContent();
    }

    /// <summary>Pushes the current project and layout into the canvas and fits it.</summary>
    public void SyncCanvas()
    {
        if (ViewModel is not { } vm)
            return;

        Canvas.Project = vm.PresentationProject;
        Canvas.Layout = vm.Layout;
        Canvas.SelectedName = vm.SelectedName;
        Canvas.Mode = vm.ViewMode;
        Canvas.Filter = vm.CanvasFilter;
        Canvas.Labels = vm.LabelPolicy;
        Canvas.AssetResolver = vm.Assets;
        Canvas.StockTemplates = vm.StockTemplates;
        Canvas.PreviewOverrides = vm.ActivePreviewOverrides;
        Canvas.HiddenByOrigin = vm.HiddenByOrigin;
        Canvas.LockedNames = vm.LockedNames;
        Canvas.PreferredSelectionNames = vm.PreferredSelectionNames;
    }

    private void OnNewClick(object? sender, RoutedEventArgs e) => ViewModel?.NewProject();

    private void OnNewLfdClick(object? sender, RoutedEventArgs e) => ViewModel?.NewDungeonFinderProject();

    private void OnAddFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.AddFrame();
    private void OnAddDesignFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignFrame();
    private void OnAddDesignTextClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignText();
    private void OnAddDesignImageClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignImage();
    private void OnBringToFrontClick(object? sender, RoutedEventArgs e) => ViewModel?.BringSelectedToFront();
    private void OnBringForwardClick(object? sender, RoutedEventArgs e) => ViewModel?.BringSelectedForward();
    private void OnSendBackwardClick(object? sender, RoutedEventArgs e) => ViewModel?.SendSelectedBackward();
    private void OnSendToBackClick(object? sender, RoutedEventArgs e) => ViewModel?.SendSelectedToBack();

    private void OnBufferedEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not TextBox { Tag: string field })
            return;
        if (e.Key == Key.Enter)
        {
            vm.Editor.CommitBufferedField(field);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.Editor.CancelBufferedField(field);
            e.Handled = true;
        }
    }

    private void OnBufferedEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is TextBox { Tag: string field })
            vm.Editor.CommitBufferedField(field);
    }

    private void OnDesignTextChanged(object? sender, TextChangedEventArgs e)
    {
        // One-way display binding prevents a canvas/property refresh from writing a stale target
        // value back into the view model mid-keystroke. Only genuine focused user input flows in.
        if (ViewModel is { } vm && sender is TextBox { IsKeyboardFocusWithin: true } textBox)
            vm.ChangeSelectedDesignText(textBox.Text ?? string.Empty);
    }

    private void OnTextStyleFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not TextBox { Tag: string field }) return;
        if (e.Key == Key.Enter)
        {
            if (field == "textStyleSize") vm.CommitTextStyleSize(); else vm.CommitTextStyleColor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelTextStyleField(field);
            e.Handled = true;
        }
    }

    private void OnTextStyleFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not TextBox { Tag: string field }) return;
        if (field == "textStyleSize") vm.CommitTextStyleSize(); else vm.CommitTextStyleColor();
    }

    private void OnCopyTextStyleClick(object? sender, RoutedEventArgs e) => ViewModel?.CopySelectedTextStyle();
    private void OnPasteTextStyleClick(object? sender, RoutedEventArgs e) => ViewModel?.PasteSelectedTextStyle();
    private void OnResetTextStyleClick(object? sender, RoutedEventArgs e) => ViewModel?.ResetTextStyleOverrides();

    private async void OnBrowseNewDesignImageClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.PrepareDesignAssetBrowse())
            return;
        if (await PickDesignImageAsync() is { } path)
        {
            var import = await ConfirmExternalAssetImportIfNeededAsync(vm, path);
            if (import is not null)
                vm.SetNewDesignImageFromFile(path, import.Value);
        }
    }

    private async void OnChangeDesignImageClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.PrepareDesignAssetBrowse())
            return;
        if (await PickDesignImageAsync() is { } path)
        {
            var import = await ConfirmExternalAssetImportIfNeededAsync(vm, path);
            if (import is not null && vm.ChangeSelectedDesignImageFromFile(path, import.Value))
                Canvas.InvalidateVisual();
        }
    }

    private async Task<string?> PickDesignImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose design artwork",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Design images (PNG, TGA, BLP)") { Patterns = ["*.png", "*.tga", "*.blp"] },
                new FilePickerFileType("PNG images") { Patterns = ["*.png"] },
                new FilePickerFileType("TGA images") { Patterns = ["*.tga"] },
                new FilePickerFileType("BLP images") { Patterns = ["*.blp"] },
            ],
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async Task<bool?> ConfirmExternalAssetImportIfNeededAsync(MainWindowViewModel vm, string path)
    {
        if (!vm.TryAssessDesignAssetSelection(path, out var requiresImport))
            return null;
        if (!requiresImport)
            return false;

        bool? result = null;
        var dialog = new Window
        {
            Title = "Import project asset?",
            Width = 480,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var import = new Button { Content = "Import", MinWidth = 88 };
        cancel.Click += (_, _) => { result = null; dialog.Close(); };
        import.Click += (_, _) => { result = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "This image is outside the FrameForge project.\n\nImport a copy into this project's assets folder?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, import },
                },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }
    private void OnCreateStateClick(object? sender, RoutedEventArgs e) => ViewModel?.CreateDesignState();
    private void OnRenameStateClick(object? sender, RoutedEventArgs e) => ViewModel?.RenameSelectedDesignState();
    private void OnDeleteStateClick(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedDesignState();
    private void OnAssignAllStatesClick(object? sender, RoutedEventArgs e) => ViewModel?.AssignSelectionToAllStates();
    private void OnAssignSelectedStateClick(object? sender, RoutedEventArgs e) => ViewModel?.AssignSelectionToSelectedState();
    private void OnRemoveSelectedStateClick(object? sender, RoutedEventArgs e) => ViewModel?.RemoveSelectionFromSelectedState();
    private void OnExpandStockClick(object? sender, RoutedEventArgs e) => ViewModel?.SetConceptualStockExpanded(true);
    private void OnCollapseStockClick(object? sender, RoutedEventArgs e) => ViewModel?.SetConceptualStockExpanded(false);

    private async void OnStockLockActionClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.ConceptualStockFramework is not { } group)
            return;
        if (!group.Locked)
        {
            vm.SetConceptualStockLocked(true);
            return;
        }
        var accepted = await ConfirmStockUnlockAsync();
        if (accepted)
            vm.SetConceptualStockLocked(false);
    }

    private async Task<bool> ConfirmStockUnlockAsync()
    {
        var result = false;
        var dialog = new Window
        {
            Title = "Unlock Blizzard framework?",
            Width = 470,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var unlock = new Button { Content = "Unlock", MinWidth = 88 };
        cancel.Click += (_, _) => dialog.Close();
        unlock.Click += (_, _) => { result = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Unlocking allows individual Blizzard components to be selected and modified. You can lock the framework again at any time.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, unlock },
                },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }

    private void OnDeleteFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.DeleteFrame();

    private void OnFitClick(object? sender, RoutedEventArgs e) => OnCanvasFitRequested(sender, EventArgs.Empty);

    private void OnRevealSelectionClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.ReportRevealSelection(Canvas.RevealSelection());

    /// <summary>
    /// Restores the active mode's own defaults.
    /// </summary>
    /// <remarks>
    /// Every visibility toggle and the label selector are independent, so a few experiments can leave
    /// the canvas in a combination no mode would ever produce. Reset gives one honest way back
    /// without switching modes, which would also work but would silently discard the mode choice.
    /// </remarks>
    private void OnResetViewClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        vm.ResetViewToModeDefaults();
    }

    private async void OnAddAssetRootClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a WoW Interface asset root",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
            vm.AddAssetRoot(path);
    }

    private void OnRemoveAssetRootClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Button { Tag: string path })
            vm.RemoveAssetRoot(path);
    }

    private void OnRefreshAssetsClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RefreshAssets();
        Canvas.InvalidateVisual();
    }

    private async void OnBrowseWoWClientClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select a World of Warcraft 3.3.5a client folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } path)
        {
            vm.SetWoWClientPath(path);
            Canvas.InvalidateVisual();
        }
    }

    private void OnClearWoWClientClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearWoWClientPath();

    private void OnRevalidateWoWClientClick(object? sender, RoutedEventArgs e) => ViewModel?.RevalidateWoWClient();

    private void OnResolveStockAssetsClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ResolveMissingStockAssets();
        Canvas.InvalidateVisual();
    }

    private void OnClearStockCacheClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ClearManagedStockCache();
        Canvas.InvalidateVisual();
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        // Projects and FrameXML side by side: the same Open action reads either, so the picker
        // must not hide the format the user is most likely to have to hand. The filter order is
        // significant - the first entry is the dialog's default - so it is sent exactly as
        // ProjectCodec declares it, and "All Supported Files" leads with both globs.
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a FrameForge project or a WoW FrameXML file",
            AllowMultiple = false,
            FileTypeFilter = ProjectCodec.OpenDialogFilters
                .Select(filter => new FilePickerFileType(filter.Label) { Patterns = [.. filter.Patterns] })
                .ToArray(),
        });

        if (files.Count > 0 && files[0].Path.LocalPath is { Length: > 0 } path)
            vm.OpenFromFile(path);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        // A read-only import has no destination yet, so Save must not shortcut past Save As.
        if (vm.ProjectPath is { Length: > 0 } existing)
        {
            vm.SaveToFile(existing);
            return;
        }

        await SaveAsAsync(vm);
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            await SaveAsAsync(vm);
    }

    private async Task SaveAsAsync(MainWindowViewModel vm)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save FrameForge project",
            SuggestedFileName = SanitizeFileName(vm.Project.Name) + ProjectCodec.FileExtension,
            DefaultExtension = ProjectCodec.FileExtension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = ProjectCodec.DialogFilters
                .Select(name => new FilePickerFileType(name) { Patterns = ["*.fforge.json"] })
                .ToArray(),
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
            vm.SaveToFile(path);
    }

    private void OnCanvasSelectionRequested(object? sender, string? name)
    {
        ViewModel?.OnCanvasSelectionRequested(name);
        Canvas.SelectedName = name;
    }

    /// <summary>
    /// A drag arrives as a model-space delta. The view model adds it to the frame's OFFSETS;
    /// anchors are never rewritten.
    /// </summary>
    private void OnCanvasFrameDragged(object? sender, FrameDragEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        if (vm.SelectedName != e.FrameName)
        {
            vm.Select(e.FrameName);
            Canvas.SelectedName = e.FrameName;
        }

        vm.DragFrame(e.FrameName, e.DeltaX, e.DeltaY);
    }

    private void OnCanvasDragCompleted(object? sender, EventArgs e) => ViewModel?.EndDrag();

    private void OnCreateGroupClick(object? sender, RoutedEventArgs e) => ViewModel?.CreateGroup();
    private void OnRenameGroupClick(object? sender, RoutedEventArgs e) => ViewModel?.RenameSelectedGroup();
    private void OnDeleteGroupClick(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedGroup();
    private void OnAddSelectionToGroupClick(object? sender, RoutedEventArgs e) => ViewModel?.AddSelectionToGroup();
    private void OnRemoveSelectionFromGroupClick(object? sender, RoutedEventArgs e) => ViewModel?.RemoveSelectionFromGroup();
    private void OnSelectCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Button { Tag: string name })
            vm.Select(name);
    }

    private void OnCanvasFitRequested(object? sender, EventArgs e)
    {
        Canvas.FitToContent();
        ViewModel?.RequestFit();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Where(c => !invalid.Contains(c))]);
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }
}
