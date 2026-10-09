using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FrameForge.Core.Geometry;
using FrameForge.Core.Export;
using FrameForge.Core.Serialization;
using FrameForge.Core.Semantics.V2;
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
            or nameof(MainWindowViewModel.SelectedNames)
            or nameof(MainWindowViewModel.ViewMode)
            or nameof(MainWindowViewModel.CanvasFilter)
            or nameof(MainWindowViewModel.LabelPolicy)
            or nameof(MainWindowViewModel.OriginFilter)
            or nameof(MainWindowViewModel.HiddenByOrigin)
            or nameof(MainWindowViewModel.LockedNames)
            or nameof(MainWindowViewModel.PreferredSelectionNames)
            or nameof(MainWindowViewModel.V2TemplateRegistry)
            or nameof(MainWindowViewModel.V2PreviewButtonState)
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
        Canvas.SelectedNames = vm.SelectedNames;
        Canvas.Mode = vm.ViewMode;
        Canvas.Filter = vm.CanvasFilter;
        Canvas.Labels = vm.LabelPolicy;
        Canvas.AssetResolver = vm.Assets;
        Canvas.StockTemplates = vm.StockTemplates;
        Canvas.PreviewOverrides = vm.ActivePreviewOverrides;
        Canvas.V2Templates = vm.IsV2Project ? vm.V2TemplateRegistry : null;
        Canvas.V2ButtonState = vm.V2PreviewButtonState;
        Canvas.HiddenByOrigin = vm.HiddenByOrigin;
        Canvas.LockedNames = vm.LockedNames;
        Canvas.PreferredSelectionNames = vm.PreferredSelectionNames;
    }

    private void OnNewClick(object? sender, RoutedEventArgs e) => ViewModel?.NewProject();

    private void OnNewV2Click(object? sender, RoutedEventArgs e) => ViewModel?.NewV2Project();

    private void OnNewLfdClick(object? sender, RoutedEventArgs e) => ViewModel?.NewDungeonFinderProject();

    private void OnAddFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.AddFrame();
    private void OnAddV2FrameClick(object? sender, RoutedEventArgs e) => ViewModel?.AddV2Control(UiNodeKind.Frame);
    private void OnAddV2TextureClick(object? sender, RoutedEventArgs e) => ViewModel?.AddV2Control(UiNodeKind.Texture);
    private void OnAddV2FontStringClick(object? sender, RoutedEventArgs e) => ViewModel?.AddV2Control(UiNodeKind.FontString);
    private void OnAddV2ButtonClick(object? sender, RoutedEventArgs e) => ViewModel?.AddV2Control(UiNodeKind.Button);
    private void OnAddV2StatusBarClick(object? sender, RoutedEventArgs e) => ViewModel?.AddV2Control(UiNodeKind.StatusBar);
    private void OnApplyV2InspectorClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyV2Inspector();
    private void OnClearV2TemplateOverridesClick(object? sender, RoutedEventArgs e) => ViewModel?.ClearV2TemplateOverrides();
    private void OnMoveV2EarlierClick(object? sender, RoutedEventArgs e) => ViewModel?.MoveV2SelectionInOrder(-1);
    private void OnMoveV2LaterClick(object? sender, RoutedEventArgs e) => ViewModel?.MoveV2SelectionInOrder(1);
    private void OnDeleteV2Click(object? sender, RoutedEventArgs e) => ViewModel?.DeleteV2Selection();
    private void OnAddDesignFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignFrame();
    private void OnAddDesignTextClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignText();
    private void OnAddDesignImageClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignImage();
    private void OnAddDesignStatusBarClick(object? sender, RoutedEventArgs e) => ViewModel?.AddDesignStatusBar();
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

    private void OnStatusBarFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not TextBox { Tag: string field }) return;
        if (e.Key == Key.Enter)
        {
            vm.CommitStatusBarField(field);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelStatusBarField(field);
            e.Handled = true;
        }
    }

    private void OnStatusBarFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not TextBox { Tag: string field }) return;
        vm.CommitStatusBarField(field);
    }

    private void OnRuntimeValueRequiredClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is CheckBox checkBox)
            vm.SetRuntimeValueRequired(checkBox.IsChecked == true);
    }

    private void OnRuntimeBindingKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (e.Key == Key.Enter)
        {
            vm.CommitRuntimeBinding();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRuntimeBinding();
            e.Handled = true;
        }
    }

    private void OnRuntimeBindingLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { RuntimeValueRequiredDraft: true } vm)
            vm.CommitRuntimeBinding();
    }

    private async void OnBrowseNewStatusBarTextureClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.PrepareDesignAssetBrowse())
            return;
        if (await PickDesignImageAsync() is { } path)
        {
            var import = await ConfirmExternalAssetImportIfNeededAsync(vm, path);
            if (import is not null && vm.SetSelectedStatusBarTextureFromFile(path, import.Value))
                Canvas.InvalidateVisual();
        }
    }

    private async void OnBrowseWowStatusBarTextureClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.IsDesignStatusBarSelected)
            return;
        var chooser = new StockTextureChooserWindow(
            vm.StatusBarStockTextureChoices(),
            vm.StatusBarTextureDraft,
            vm.WoWClientStatus,
            vm.HasWoWClientSelection);
        await chooser.ShowDialog(this);
        if (chooser.SelectedInterfacePath is { } reference && vm.SetSelectedStatusBarTextureFromWow(reference))
            Canvas.InvalidateVisual();
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

    /// <summary>
    /// Opens the compact state-membership chooser for the current selection.
    /// </summary>
    /// <remarks>
    /// The dialog is built from the chooser view model, so its rows are the project's current
    /// authored states - All States first, nothing hardcoded - and it carries the selection's
    /// starting point: pre-checked when the selection shares one membership, Mixed and empty when
    /// it does not. Nothing is written until Apply: Cancel discards the chooser, which is what
    /// keeps a cancelled dialog from touching the project or dirtying it.
    /// </remarks>
    private async void OnChooseStatesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || vm.CreateStateMembershipChooser() is not { } chooser)
            return;

        var (dialog, applyRequested) = CreateStateMembershipDialog(chooser);
        await dialog.ShowDialog(this);
        if (applyRequested())
            vm.ApplyStateMembership(chooser);
    }

    /// <summary>
    /// Builds the chooser dialog for one <see cref="StateMembershipChooser"/> and reports whether
    /// Apply was pressed when it closes. Separate from the click handler so the dialog's bindings
    /// can be exercised without a modal owner in the way.
    /// </summary>
    internal static (Window Dialog, Func<bool> ApplyRequested) CreateStateMembershipDialog(StateMembershipChooser chooser)
    {
        var rows = new StackPanel { Spacing = 2 };
        foreach (var option in chooser.Options)
        {
            var box = new CheckBox { Content = option.Name };
            box.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(StateMembershipOption.IsChecked))
            {
                Source = option,
                Mode = BindingMode.TwoWay,
            });
            box.Bind(InputElement.IsEnabledProperty, new Binding(nameof(StateMembershipOption.IsEnabled))
            {
                Source = option,
                Mode = BindingMode.OneWay,
            });
            rows.Children.Add(box);
        }

        var mixedNote = new TextBlock
        {
            Text = chooser.MixedNote,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#F2C14E")),
            IsVisible = chooser.IsMixed,
        };
        var lockNote = new TextBlock
        {
            Text = chooser.LockNote,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#8FA3B0")),
        };
        var validation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#E5796B")),
        };
        validation.Bind(TextBlock.TextProperty, new Binding(nameof(StateMembershipChooser.ValidationMessage))
        {
            Source = chooser,
            Mode = BindingMode.OneWay,
        });

        var cancel = new Button { Content = "Cancel", MinWidth = 88 };
        var apply = new Button { Content = "Apply", MinWidth = 88, IsEnabled = chooser.CanApply };
        apply.Bind(InputElement.IsEnabledProperty, new Binding(nameof(StateMembershipChooser.CanApply))
        {
            Source = chooser,
            Mode = BindingMode.OneWay,
        });

        var applyRequested = false;
        var dialog = new Window
        {
            Title = "State Membership",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "State Membership", FontWeight = FontWeight.Bold },
                    mixedNote,
                    new TextBlock
                    {
                        Text = "Applies to the whole selection. Locked objects are kept as they are.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#8FA3B0")),
                    },
                    rows,
                    lockNote,
                    validation,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, apply },
                    },
                },
            },
        };
        cancel.Click += (_, _) => dialog.Close();
        apply.Click += (_, _) => { applyRequested = true; dialog.Close(); };
        return (dialog, () => applyRequested);
    }

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

    private async void OnExportWow335Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        if (vm.IsV2Project)
        {
            if (vm.V2Document is not { } document) return;
            var plan = V2FrameXmlExporter.Build(document,
                string.IsNullOrWhiteSpace(vm.ProjectPath) ? null : vm.ProjectPath, vm.V2TemplateRegistry);
            var v2Errors = plan.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
            if (v2Errors.Length > 0)
            {
                vm.Status = "Export blocked: " + string.Join(" ", v2Errors.Take(3).Select(item => $"{item.Code}: {item.Message}"));
                return;
            }
            var destinations = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose an empty or existing WoW 3.3.5a export directory",
                AllowMultiple = false,
            });
            if (destinations.Count > 0 && destinations[0].Path.LocalPath is { Length: > 0 } v2Destination)
                try { vm.ExportV2(v2Destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { vm.Status = $"Export failed safely: {ex.Message}"; }
            return;
        }

        var validation = Wow335ExportBuilder.Build(vm.Project,
            string.IsNullOrWhiteSpace(vm.ProjectPath) ? null : vm.ProjectPath);
        var errors = validation.Diagnostics.Where(d => d.Severity == ExportSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            vm.Status = $"Export blocked: {string.Join(" ", errors.Take(3).Select(d => $"{d.Code}: {d.Message}"))}";
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty or existing WoW 3.3.5a export directory",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].Path.LocalPath is not { Length: > 0 } destination)
            return;

        try
        {
            var result = Wow335Exporter.Export(vm.Project,
                string.IsNullOrWhiteSpace(vm.ProjectPath) ? null : vm.ProjectPath, destination);
            vm.Status = result.Summary + $" Output: {destination}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            vm.Status = $"Export failed safely: {ex.Message}";
        }
    }

    private async void OnAssociateFunctionalFrameXmlClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Associate authoritative functional FrameXML",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WoW FrameXML") { Patterns = ["*.xml"] }],
        });
        if (files.Count > 0 && files[0].Path.LocalPath is { Length: > 0 } path)
            vm.AssociateFunctionalSource(path);
    }

    private async void OnExportFunctionalDesignClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanExportFunctionalDesign: true } vm)
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty directory for the functional WoW package",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } destination)
            vm.ExportFunctionalDesign(destination);
    }

    private async void OnExportLayoutChangesClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanExportLayoutChanges: true } vm)
            return;

        var sourceName = vm.Project.Source?.FileName ?? "layout.xml";
        var suggestedName = Path.GetFileNameWithoutExtension(sourceName) + ".layout.xml";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Layout Changes",
            SuggestedFileName = suggestedName,
            DefaultExtension = "xml",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("WoW FrameXML") { Patterns = ["*.xml"] }],
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
            vm.ExportLayoutChanges(path);
    }

    private void OnCanvasSelectionRequested(object? sender, CanvasSelectionEventArgs e)
    {
        ViewModel?.OnCanvasSelectionRequested(e.FrameName, e.Additive);
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
            vm.Select(e.FrameName);

        vm.DragFrame(e.FrameName, e.DeltaX, e.DeltaY);
    }

    private void OnCanvasDragCompleted(object? sender, EventArgs e) => ViewModel?.EndDrag();

    /// <summary>Runs the align or distribute command named by the button's Tag.</summary>
    private void OnArrangeClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Button { Tag: string name }
            && Enum.TryParse<SelectionArrangeCommand>(name, out var command))
        {
            vm.ArrangeSelection(command);
        }
    }

    /// <summary>Drops the extra members of a multi-selection, keeping the primary.</summary>
    private void OnCollapseSelectionClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.Select(ViewModel.SelectedName);

    /// <summary>
    /// Makes every tree-row click a selection change the view model owns.
    /// </summary>
    /// <remarks>
    /// The tree is the authoritative source of exact-object selection, so a plain click always
    /// resolves to <see cref="MainWindowViewModel.Select"/> - it replaces the selection with
    /// exactly the clicked object, which is what collapses a multi-selection back to one. The
    /// gesture is marked handled because Avalonia's TreeView highlights a single item and raises
    /// no event when the clicked row is already the highlighted primary; routing the click
    /// ourselves means re-clicking the primary still collapses, and the highlight follows the
    /// view model's own re-synchronization.
    /// <para>
    /// A Ctrl/Cmd/Shift click adds or removes one object via
    /// <see cref="MainWindowViewModel.ToggleSelection"/>, and would otherwise collapse to the
    /// clicked row.
    /// </para>
    /// <para>
    /// A click on an expander gutter or a blank area is left alone: those are navigation and
    /// deselect actions, and swallowing them would break expanding a collapsed group.
    /// </para>
    /// </remarks>
    private void OnFrameTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Visual source)
            return;

        // The expander is a ToggleButton inside the same row, so it would otherwise read as
        // "the user clicked this frame". Expanding is navigation, not selection.
        if (source is ToggleButton)
            return;

        if (FindTreeNode(source) is not { } node)
            return;

        if (LayoutCanvas.IsAdditiveModifier(e.KeyModifiers))
            vm.ToggleSelection(node.Name);
        else
            vm.Select(node.Name);
        e.Handled = true;
    }

    /// <summary>The nearest tree row's node at or above this visual, or null.</summary>
    private static FrameTreeNode? FindTreeNode(Visual visual)
    {
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (current is StyledElement { DataContext: FrameTreeNode node })
                return node;
        }

        return null;
    }

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
