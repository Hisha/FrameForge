using System.ComponentModel;
using Avalonia.Controls;
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
            or nameof(MainWindowViewModel.SelectedName))
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

        Canvas.Project = vm.Project;
        Canvas.Layout = vm.Layout;
        Canvas.SelectedName = vm.SelectedName;
    }

    private void OnNewClick(object? sender, RoutedEventArgs e) => ViewModel?.NewProject();

    private void OnLoadExampleClick(object? sender, RoutedEventArgs e) => ViewModel?.LoadNativeHuntsExample();

    private void OnAddFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.AddFrame();

    private void OnDeleteFrameClick(object? sender, RoutedEventArgs e) => ViewModel?.DeleteFrame();

    private void OnFitClick(object? sender, RoutedEventArgs e) => OnCanvasFitRequested(sender, EventArgs.Empty);

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open FrameForge project",
            AllowMultiple = false,
            FileTypeFilter = ProjectCodec.DialogFilters
                .Select(name => new FilePickerFileType(name) { Patterns = ["*.fforge.json", "*.json"] })
                .ToArray(),
        });

        if (files.Count > 0 && files[0].Path.LocalPath is { Length: > 0 } path)
            vm.OpenFromFile(path);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

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
            DefaultExtension = "fforge.json",
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