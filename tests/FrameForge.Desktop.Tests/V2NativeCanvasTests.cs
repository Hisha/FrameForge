using Avalonia.Controls;
using Avalonia.Headless;
using FrameForge.Core.Geometry;
using FrameForge.Core.Semantics.V2;
using FrameForge.Core.Viewing;
using FrameForge.Desktop.Controls;
using FrameForge.Desktop.Rendering;
using FrameForge.Desktop.Views;
using Xunit;

namespace FrameForge.Desktop.Tests;

public sealed class V2NativeCanvasTests
{
    [Fact]
    public async Task KeyboardEditingIsSuppressedForTextAndNumericInputs()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        var result = await session.Dispatch(() =>
        {
            var inputs = new StackPanel { Children = { new TextBox(), new NumericUpDown(), new ComboBox() } };
            var window = new Window { Content = inputs, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                window.Show();
                window.CaptureRenderedFrame();
                return (inputs.Children.All(control => MainWindow.IsEditorInput(control)),
                    MainWindow.IsEditorInput(new LayoutCanvas()));
            }
            finally { window.Close(); }
        }, CancellationToken.None);

        Assert.True(result.Item1);
        Assert.False(result.Item2);
    }

    [Fact]
    public async Task SingleV2SelectionHasEightConstantPixelResizeHandles()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "Host", 1024, 768);
        var created = editor.CreateControl(document, UiNodeKind.Frame,
            OwnerReference.Root(document.CompositionRoots[0].Id), "Frame");
        var id = created.AffectedId!.Value;
        document = editor.UpdateGeometry(created.Document, id, 200, 100, 20, -10).Document;
        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(document.CompositionRoots[0]));

        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        var handles = await session.Dispatch(() =>
        {
            var canvas = new LayoutCanvas
            {
                V2Layout = layout,
                SelectedName = id.Value,
                SelectedNames = [id.Value],
            };
            var window = new Window { Content = canvas, Width = 640, Height = 480, ShowActivated = false };
            try
            {
                window.Show();
                window.CaptureRenderedFrame();
                return canvas.GetV2ResizeHandles();
            }
            finally { window.Close(); }
        }, CancellationToken.None);

        Assert.Equal(8, handles.Count);
        Assert.All(handles.Values, rect => Assert.Equal((9d, 9d), (rect.Width, rect.Height)));
    }

    [Fact]
    public async Task HeadlessCanvasRendersAndHitTestsNativeLayoutWithoutV1Project()
    {
        var editor = new UiDocumentEditor();
        var document = UiDocumentFactory.Create("Root", "ModuleHost", 1024, 768);
        var root = document.CompositionRoots[0];
        var created = editor.CreateControl(document, UiNodeKind.StatusBar, OwnerReference.Root(root.Id), "Progress");
        var id = created.AffectedId!.Value;
        document = editor.UpdateGeometry(created.Document, id, 240, 24, 30, -20).Document;
        var node = document.Nodes.Single();
        document = editor.UpdateProperties(document, id, node.AuthoredProperties with
        {
            StatusBar = new StatusBarProperties
            {
                Minimum = 0, Maximum = 100, Value = 60,
                FillColor = new UiColor(0.1, 0.8, 0.2), BackgroundColor = new UiColor(0.05, 0.05, 0.05),
            },
        }).Document;
        var layout = UiLayoutResolver.Resolve(document, UiPreviewHost.FromDesignRoot(root));

        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        var result = await session.Dispatch(() =>
        {
            var canvas = new LayoutCanvas { Project = null, V2Layout = layout, Mode = CanvasViewMode.PREVIEW };
            var window = new Window { Content = canvas, Width = 640, Height = 480, ShowActivated = false };
            try
            {
                window.Show();
                window.CaptureRenderedFrame();
                canvas.FitToContent();
                window.CaptureRenderedFrame();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.CaptureRenderedFrame();
                var rect = layout.Elements[id].Rect!.Value;
                var point = canvas.Viewport.ModelToCanvas(rect.Center, canvas.Origin);
                return (Assert.IsType<CanvasRenderTrace>(canvas.LastRenderTrace), canvas.HitTestCandidates(point.X, point.Y));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

        Assert.True(result.Item1.VisualContentExecuted);
        Assert.Equal(1, result.Item1.VisualAttemptsByKind.GetValueOrDefault(FrameForge.Core.Models.FrameKind.STATUSBAR));
        Assert.Equal(id.Value, result.Item2[0]);
    }
}
