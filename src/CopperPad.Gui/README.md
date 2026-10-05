# CopperPad.Gui

`CopperPad.Gui` is the end-user Avalonia desktop app for testing, mapping, and calibrating controllers through CopperPad.

The charcoal-and-copper workspace keeps controller selection, navigation, and save state visible while editing. Test supports built-in mappings immediately, with a vector gamepad and a labeled control grid. Mapping offers guided capture, per-control remapping, undo/redo, and an expandable raw HID editor. Calibration previews raw and adjusted movement with deadzone feedback and editable numeric settings. Diagnostics includes pause and copy controls.

The compact header shows the selected controller, a connection badge, and the active mapping source. Save state appears in the save bar; connection transport and full mapping details are in Diagnostics. Branding stays in the sidebar, and the footer appears only for action feedback.

The workspace keeps static explanations in keyboard-accessible **?** help popovers. Mapping rows use a check for assigned controls and a dash for optional unassigned controls, with totals in the group headings. Assigned rows expand to show raw source details and offer Clear; **More…** contains Clear all and Advanced. Undo/redo use labeled, focusable icon buttons. Active capture instructions and validation errors stay visible.

Test places accessible numeric readings beside the diagram’s stick plots and trigger meters; **Control grid** provides the alternate labeled view with local readings. Calibration uses a **Rest → Range → Review** strip, one current instruction, and labeled raw/adjusted/deadzone markers. Deadzone and saturation explanations are available beside their controls.

Edits are in-memory drafts. Device refresh, switching, and reconnecting the same device retain drafts for the current app session. Valid edits are previewed locally after a short debounce; **Save changes** writes the existing profile document. **Revert** restores the baseline. Closing with unsaved changes offers Save all, Discard, and Cancel. Drafts do not survive an app crash or discarded close.

Disconnected controllers with unsaved edits stay in the sidebar for editing, saving, or reverting. Their undo/redo history remains accessible through refresh; device filters also retain edited HID devices. A reader or preview failure cannot prevent restoring the editor. **Save all changes** appears when other controllers have pending edits. A custom mapping needs at least one assignment before it can be saved or previewed, so editing a name cannot replace a working built-in mapping with an empty override. Validation stays beside the profile name, assignments, and advanced fields.

**Remap** starts capture directly for the selected control, with a **Remapping** label. Retry replaces that control’s assignment; releasing accepted input or choosing Continue completes the remap. Skip leaves existing assignments unchanged, and Stop retains accepted assignments. **Guided setup** walks through the remaining controls with numbered progress. Both workflows retain neutral detection and capture delays.

Calibration requires a raw binding: create custom mappings first when using a built-in mapping without editable sources. Range capture is temporary until accepted; changing calibration axes or leaving the page cancels an unfinished capture. Import explicitly replaces the saved profile document after review; export includes saved profiles only.

Calibration progresses through **Capture rest**, **Start range capture**, **Review range**, and **Accept range**. Failed captures stay on the current step. Review offers retry and cancellation; digital triggers show their 0/1 output and do not need analog range adjustments.

During capture, the graph retains the current draft calibration. Review labels a valid captured range as **Capture preview · Not applied** and shows the current draft output alongside it. Incomplete ranges do not generate output previews: sticks must reach both sides of center, and triggers must stay neutral at captured rest. Every raw report contributes to range capture, including extremes between UI refreshes; cancellation discards pending samples. Saturation controls support the existing profile range down to 0.01.

Keyboard shortcuts: Ctrl+S saves, Ctrl+Z undoes, and Ctrl+Y/Ctrl+Shift+Z redoes mapping edits. Text fields retain their native undo behavior. Controls support Tab navigation and keyboard activation.

Errors appear in a persistent panel above the workspace, including failures from saving, importing/exporting, device scanning, draft preview, assignment, and calibration. The panel stays visible while scrolling and offers a retry or a link to the affected fields. **Details…** lists all pending errors with selectable text and a copy action. Draft validation also appears on every page with **Fix mapping**; resolving the issue or reverting the draft clears it.

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
