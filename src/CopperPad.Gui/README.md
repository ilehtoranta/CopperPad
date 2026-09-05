# CopperPad.Gui

`CopperPad.Gui` is the end-user Avalonia desktop app for testing, mapping, and calibrating controllers through CopperPad.

The charcoal-and-copper workspace keeps controller selection, navigation, and save state visible while editing. Test supports built-in mappings immediately, with a vector gamepad and a labeled control grid. Mapping offers guided capture, per-control remapping, undo/redo, and an expandable raw HID editor. Calibration previews raw and adjusted movement with deadzone feedback and editable numeric settings. Diagnostics includes pause and copy controls.

Edits are in-memory drafts. Device refresh, switching, and reconnecting the same device retain drafts for the current app session. Valid edits are previewed locally after a short debounce; **Save changes** writes the existing profile document. **Revert** restores the baseline. Closing with unsaved changes offers Save all, Discard, and Cancel. Drafts do not survive an app crash or discarded close.

Calibration requires a raw binding: create custom mappings first when using a built-in mapping without editable sources. Range capture is temporary until accepted; changing calibration axes or leaving the page cancels an unfinished capture. Import explicitly replaces the saved profile document after review; export includes saved profiles only.

Keyboard shortcuts: Ctrl+S saves, Ctrl+Z undoes, and Ctrl+Y/Ctrl+Shift+Z redoes mapping edits. Text fields retain their native undo behavior. Controls support Tab navigation and keyboard activation.

Run `dotnet test tests/CopperPad.Gui.Tests -c Release` for editor and Avalonia headless coverage. The render tests write page captures to `artifacts/ui-review` at 900×600 and 1180×760, using 100% and 150% scaling. Synthetic devices do not replace a hands-on controller capture/calibration check.

Publish and smoke-test the Windows release artifact with:

```powershell
CopperPad\scripts\SmokeTest-Gui.ps1 -Configuration Release -Runtime win-x64
```

The executable also supports a release smoke mode:

```powershell
CopperPad.Gui.exe --smoke-test
```

Smoke mode opens the normal main-window startup path and exits automatically. A non-zero exit code means startup failed; details are written to the per-user CopperPad crash log.
