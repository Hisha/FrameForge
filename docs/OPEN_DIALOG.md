# The Open dialog and its file-type filters

## What FrameForge sends

`MainWindow.OnOpenClick` calls `IStorageProvider.OpenFilePickerAsync` with the filters declared
by `ProjectCodec.OpenDialogFilters`, in that exact order:

| # | Label | Globs |
|---|-------|-------|
| 0 | `All Supported Files` | `*.fforge.json`, `*.xml` |
| 1 | `FrameForge Projects (*.fforge.json)` | `*.fforge.json` |
| 2 | `WoW FrameXML (*.xml)` | `*.xml` |
| 3 | `All Files (*)` | `*.*` |

**Order is part of the contract.** The first entry is the dialog's default selection, so
"All Supported Files" leads: Open reads either format, and clicking Open then navigating to a
directory should show whichever kind of file is actually there.

## How each platform receives it

The dialog itself is supplied by the platform, and Avalonia's `Window.StorageProvider` resolves
to one of three implementations, in this order:

| Order | Provider | Assembly | Used when |
|-------|----------|----------|-----------|
| 0 | `Avalonia.FreeDesktop.DBusSystemDialog` | `Avalonia.FreeDesktop` | XDG desktop portal is reachable |
| 1 | `Avalonia.X11.NativeDialogs.GtkSystemDialog` | `Avalonia.X11` | no portal, but GTK is available |
| 2 | `Avalonia.Dialogs.ManagedStorageProvider` | `Avalonia.Dialogs` | neither of the above |

`FallbackStorageProvider` walks that list and uses the first that reports `CanOpen`. On a
typical Linux desktop session the portal is present, so the dialog is the portal's.

## The Linux limitation

When the portal serves the request, Avalonia makes exactly one call:

```
org.freedesktop.portal.FileChooser.OpenFile(
    parent_window, title,
    options = { filters: [(name, [(mime, glob), ...]), ...],
                multiple: false,
                handle_token: "..." })
```

and then the dialog belongs to a remote backend. Consequences:

* **The filter list is sent once.** There is no second call.
* **The portal has no "filter changed" method.** `org.freedesktop.portal.FileChooser` exposes
  only `OpenFile`, `SaveFile` and `SaveFiles`.
* **Therefore re-listing the current directory when the user changes the file-type combo is
  entirely the backend's behaviour.** It is not reachable from application code, through
  Avalonia or directly over D-Bus. Avalonia's API has no way to select a filter, and no way to
  ask the dialog to refresh.

The observable symptom is that on some portal backends changing the combo leaves the file list
unchanged until the directory is re-entered or the dialog is otherwise nudged.

### What this means for FrameForge

This is a platform limitation, not a defect in the filter definitions, and it is why the fix is
to make the *default* filter match both formats rather than to try to drive the combo. After the
change, the common case - click Open, navigate, see the file - needs no interaction with the
file-type selector at all. The narrower filters remain for users who want one format only.

Two further observations from reading the wire traffic, recorded so they are not re-investigated:

* Avalonia sends an **empty MIME type** beside every glob (`("", "*.xml")`). That is legal per
  the portal specification, which permits matching on either half of the pair, and it is what
  backends that prefer the glob expect. FrameForge does not supply explicit MIME types: doing so
  could narrow matching on backends that prefer the MIME half, and the benefit could not be
  verified without driving the dialog interactively.
* Avalonia does not send the optional `current_filter` option, so the backend preselects the
  first filter in the list. That is the mechanism that makes filter order significant.

## Verification

`SmokeTest` asserts the filter configuration and re-opens both formats through
`MainWindowViewModel.OpenFromFile`, which is the method the picker calls. The on-the-wire payload
was verified by capturing `org.freedesktop.portal.FileChooser` traffic with `dbus-monitor` while
opening the dialog with the real `ProjectCodec.OpenDialogFilters`.

Interactively confirming whether a given portal backend refreshes on a combo change requires
driving the dialog, which is manual verification rather than an automated check.