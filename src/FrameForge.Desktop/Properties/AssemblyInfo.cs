using System.Runtime.CompilerServices;

// The desktop tests assert on render geometry (e.g. the shared StatusBarRendering helper) and on
// small internal helpers; exposing only what the tests depend on keeps the seam explicit.
[assembly: InternalsVisibleTo("FrameForge.Desktop.Tests")]