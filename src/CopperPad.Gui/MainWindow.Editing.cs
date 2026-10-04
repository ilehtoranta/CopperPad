using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using CopperPad;

namespace CopperPad.Gui;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, EditorSession> _sessions = new(StringComparer.Ordinal);
    private EditorSession? _session;
    private readonly TextBlock _deviceTitle = TextBlock(), _deviceSubtitle = TextBlock(), _saveState = TextBlock(), _previewText = TextBlock(), _guidedProgress = TextBlock(), _liveNumbers = TextBlock();
    private readonly TextBlock _calibrationStep = TextBlock(), _calibrationValues = TextBlock();
    private readonly Border _connectionBadge = new();
    private readonly TextBox _profileName = new();
    private readonly TextBlock _profileNameError = TextBlock(), _assignmentError = TextBlock();
    private readonly Button _saveAllButton = new() { Content = "Save all changes" };
    private Border? _gamepadCard;
    private readonly Button _setupButton = new() { Content = "Set up controller", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _revertButton = new() { Content = "Revert" }, _undoButton = new() { Content = "Undo" }, _redoButton = new() { Content = "Redo" };
    private readonly Button _clearAllButton = new() { Content = "Clear all" }, _openAdvancedButton = new() { Content = "Advanced…" };
    private readonly Button _backGuided = new() { Content = "Back", IsEnabled = false }, _retryGuided = new() { Content = "Retry", IsEnabled = false };
    private readonly StackPanel _mappingRows = new() { Spacing = 6 };
    private readonly Expander _advancedEditor = new() { Header = "Advanced · Raw HID binding editor", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly GamepadView _gamepad = new();
    private readonly CalibrationGraph _calibrationGraph = new();
    private readonly NumericUpDown _deadzoneNumber = new() { Minimum = 0, Maximum = .95m, Increment = .01m, FormatString = "0.00", Width = 110, Height = 36, ShowButtonSpinner = false };
    private readonly NumericUpDown _saturationNumber = new() { Minimum = .01m, Maximum = 1, Increment = .01m, FormatString = "0.00", Width = 110, Height = 36, ShowButtonSpinner = false };
    private bool _loadingEditor, _loadingCalibration, _rangeReview, _diagnosticsPaused, _allowClose, _closePending, _saving;
    private int _previewVersion;
    internal Func<string, string, string[], Task<string?>>? DialogHandler { get; set; }

    private void CommitEditorChange()
    {
        if (_session == null || _draftProfile == null) return;
        _session.Edit(_draftProfile);
        UpdateEditorState();
        UpdateValidation();
        SchedulePreview();
    }

    private void UpdateEditorState()
    {
        _loadingEditor = true;
        if (_profileName.Text != _draftProfile?.Name) _profileName.Text = _draftProfile?.Name ?? "";
        _loadingEditor = false;
        _saveProfileButton.IsVisible = true;
        _saveProfileButton.Content = "Save changes";
        _saveProfileButton.IsEnabled = _session is { IsDirty: true } && _session.Issues.Count == 0 && !_guidedMappingActive && !_calibrationActive && !_rangeReview;
        _revertButton.IsEnabled = _session?.IsDirty == true;
        _undoButton.IsEnabled = _session?.CanUndo == true;
        _redoButton.IsEnabled = _session?.CanRedo == true;
        _profileName.IsEnabled = _session != null;
        _advancedEditor.IsEnabled = _openAdvancedButton.IsEnabled = _session != null;
        _clearAllButton.IsEnabled = _draftProfile?.Bindings.Count > 0 && !_guidedMappingActive;
        _calibrationTargetBox.IsEnabled = _session != null;
        var other = _sessions.Values.Count(x => x != _session && x.IsDirty);
        _saveState.Text = (_session?.IsDirty == true ? "Unsaved changes" : _session == null ? "No controller selected" : "Saved") + (other > 0 ? $" · {other} other unsaved controller(s)" : "");
        _saveState.Foreground = _session?.IsDirty == true || other > 0 ? CopperTheme.Warning : CopperTheme.Muted;
        _saveAllButton.IsVisible = other > 0;
        _saveAllButton.IsEnabled = !_guidedMappingActive && !_calibrationActive && !_rangeReview && _sessions.Values.Where(x => x.IsDirty).All(x => x.Issues.Count == 0);
        _startGuidedMappingButton.IsEnabled = _selectedDevice != null && !_guidedMappingActive;
        UpdateErrorBanner();
    }

    private void RestoreSession()
    {
        _draftProfile = _session?.Draft;
        _rangeReview = false;
        UpdateBindingList(); UpdateValidation(); LoadCalibrationFromTarget(); UpdateSelectedDeviceDetails();
    }

    private void UndoEdit(bool redo)
    {
        if (_session == null) return;
        StopGuidedMapping("Editing history restored.");
        if (redo) _session.Redo(); else _session.Undo();
        RestoreSession();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_saving || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.S) { e.Handled = true; _ = SaveDraftProfileAsync(); }
        // Text fields retain their native text-editing undo history.
        if (e.Source is TextBox) return;
        if (e.Key is Key.Z or Key.Y) { UndoEdit(e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
    }

    private void SchedulePreview()
    {
        var version = ++_previewVersion;
        DispatcherTimer.RunOnce(() => TryRunUiAction("Preview failed", () =>
        {
            if (_disposed || version != _previewVersion || _guidedMappingActive || _calibrationActive || _rangeReview) return;
            if (_session is { IsDirty: true })
            {
                if (_session.Issues.Count > 0) { SetOutputCaption("Preview unchanged", "Live output retains the last valid mapping. Correct the errors above to preview changes.", true); return; }
                _host.UpdateProfiles(_session.Preview(_profiles));
                SetOutputCaption("Draft preview", "Live output uses your current draft. Save changes to keep it.");
            }
            else
            {
                _host.UpdateProfiles(_profiles);
                UpdateOutputLabel();
            }
            UpdateSelectedDeviceDetails();
            ClearUiError("Preview failed", _selectedDevice?.Id);
        }), TimeSpan.FromMilliseconds(200));
    }

    private async Task<bool> SaveSessionAsync(EditorSession session)
    {
        if (_saving) return false;
        if (!session.IsDirty) return true;
        if (session.Issues.Count != 0) { SetStatus($"Cannot save {session.Draft.Name}: {session.Issues[0].Message}"); UpdateErrorBanner(); return false; }
        var profile = session.Draft with { UpdatedAt = DateTimeOffset.UtcNow };
        var candidate = session.WithSavedDraft(_profiles, profile);
        _saving = true;
        IsEnabled = false;
        try
        {
            await _profileStore.SaveAsync(candidate);
            _profiles = candidate;
            session.MarkSaved(profile);
            ClearUiError("save", session.Device.Id);
            ClearUiError("load");
            if (session == _session) RestoreSession();
            SchedulePreview();
            SetStatus($"Saved {profile.Name}.");
            ApplyDeviceFilter();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetStatus($"Could not save {profile.Name}: {ex.Message}. Your draft is retained; retry saving.");
            ShowUiError("save", session.Device.Id, "Save failed", $"{session.Device.ProductName}: Your draft is retained. Check the profile location and retry.", ex.Message + "\nProfile document: " + _profileStore.Path, "Retry save", async () => {
                NavigateToError(session.Device.Id, session.SelectedPage);
                await SaveSessionAsync(session);
            });
            return false;
        }
        finally { _saving = false; IsEnabled = true; }
    }

    private void UpdateOutputLabel()
    {
        if (_selectedDevice == null) { SetOutputCaption("", ""); return; }
        if (_session?.HasSavedProfile == true) { SetOutputCaption("Custom mapping", $"Saved profile: {_session.Baseline.Name}"); return; }
        var mapping = GetMappingInfo(_selectedDevice);
        if (mapping == null || mapping.Source.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase)) SetOutputCaption("Unmapped", "Set up a custom mapping to test this controller.");
        else if (mapping.Source.Contains("Fallback", StringComparison.OrdinalIgnoreCase)) SetOutputCaption("Fallback · Unverified", "The generic HID mapping has not been verified for this controller.", true);
        else SetOutputCaption("Built-in mapping", $"Built-in mapping: {mapping.Source} · {mapping.Name}");
    }

    private void SetOutputCaption(string caption, string description, bool warning = false)
    {
        _previewText.Text = caption;
        _previewText.IsVisible = !string.IsNullOrEmpty(caption);
        _previewText.Foreground = warning ? CopperTheme.Warning : caption == "Draft preview" ? CopperTheme.Copper : CopperTheme.Muted;
        ToolTip.SetTip(_previewText, description);
        Avalonia.Automation.AutomationProperties.SetName(_previewText, string.IsNullOrEmpty(description) ? caption : $"{caption}. {description}");
    }

    private void UpdateConnectionBadge(bool connected)
    {
        _connectionBadge.IsVisible = _selectedDevice != null;
        _deviceSubtitle.Text = connected ? "Connected" : "Disconnected";
        _deviceSubtitle.Foreground = connected ? CopperTheme.Success : CopperTheme.Warning;
        ToolTip.SetTip(_connectionBadge, connected ? $"Connection: {_selectedDevice?.Transport}" : "Reconnect this controller to test input.");
        Avalonia.Automation.AutomationProperties.SetName(_deviceSubtitle, connected ? $"Controller connected via {_selectedDevice?.Transport}" : "Controller disconnected. Reconnect to test input.");
    }

    private async Task SaveAllDraftsAsync()
    {
        if (_guidedMappingActive || _calibrationActive || _rangeReview) { SetStatus("Finish or cancel capture before saving."); return; }
        foreach (var session in _sessions.Values.Where(x => x.IsDirty).ToArray())
            if (!await SaveSessionAsync(session)) return;
    }

    private async Task<bool> ResolveDirtySessionsAsync(bool discardImmediately = true)
    {
        var dirty = _sessions.Values.Where(s => s.IsDirty).ToArray();
        if (dirty.Length == 0) return true;
        var choice = await AskAsync("Unsaved changes", $"You have changes for {dirty.Length} controller(s).", "Save all", "Discard", "Cancel");
        if (choice == "Save all")
        {
            if (_guidedMappingActive) StopGuidedMapping("Completed assignments retained.");
            if (_rangeReview || _calibrationActive) CancelCalibration();
            foreach (var session in dirty) if (!await SaveSessionAsync(session)) return false;
            return true;
        }
        if (choice != "Discard") return false;
        if (discardImmediately)
        {
            foreach (var session in dirty) { session.Revert(); ClearEditingErrors(session.Device.Id); }
            RestoreSession();
        }
        return true;
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_saving) { e.Cancel = true; return; }
        if (_allowClose || !_sessions.Values.Any(s => s.IsDirty)) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        try { if (await ResolveDirtySessionsAsync()) { _allowClose = true; Close(); } }
        finally { _closePending = false; }
    }

    private async Task<string?> AskAsync(string title, string message, params string[] choices)
    {
        if (DialogHandler != null) return await DialogHandler(title, message, choices);
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children = { Heading(title, 21), Text(message) } };
        panel.Children.Add(Actions(choices.Select(choice => Button(choice, (_, _) => dialog.Close(choice))).ToArray()));
        dialog.Content = panel;
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task ImportDocumentAsync(ControllerProfileSet importedProfiles)
    {
        var replacementSaved = false;
        try
        {
            if (importedProfiles.Profiles.Any(p => ProfileEditor.ValidateProfile(p, int.MaxValue).Count > 0)) throw new InvalidOperationException("The imported document contains invalid profiles.");
            if (await AskAsync("Replace saved profiles?", $"This replaces {_profiles.Profiles.Count} saved profiles with {importedProfiles.Profiles.Count} imported profiles.", "Replace", "Cancel") != "Replace") return;
            if (!await ResolveDirtySessionsAsync(discardImmediately: false)) return;
            await _profileStore.SaveAsync(importedProfiles);
            replacementSaved = true;
            _profiles = importedProfiles;
            _sessions.Clear();
            _session = null;
            var selected = _selectedDevice;
            _selectedDevice = null;
            SelectDevice(selected);
            SetStatus("Imported profiles.");
            ClearUiError("import");
            ClearUiError("load");
            foreach (var key in _errors.Keys.Where(k => k.DeviceId != null).ToArray()) _errors.Remove(key);
            UpdateErrorBanner();
        }
        catch (Exception ex)
        {
            if (replacementSaved) ShowFailure("import", "Imported profiles need a refresh", "The imported document was saved, but the controller could not be refreshed. Refresh devices to continue.", ex, "Refresh devices", () => { RefreshDevices(); return Task.CompletedTask; });
            else ShowImportFailure(ex);
            throw;
        }
    }

    private void RestartGuidedTarget(int index)
    {
        if (!_guidedMappingActive) return;
        _guidedTargetIndex = index;
        _guidedIgnoredSourceKeys.Clear();
        BeginGuidedArming();
    }

    private ControllerElement CalibrationTarget => _calibrationTargetBox.SelectedItem is MappingTargetItem item ? item.Element : ControllerElement.LeftStickX;

    private void CalibrationSettingChanged()
    {
        if (_loadingCalibration) return;
        _loadingCalibration = true;
        _deadzoneNumber.Value = (decimal)_deadzoneSlider.Value;
        _saturationNumber.Value = (decimal)_saturationSlider.Value;
        _loadingCalibration = false;
        if (!_rangeReview && !_calibrationActive && GetCalibrationBinding() is { } binding && IsAnalogSource(binding) && _draftProfile != null)
        {
            var axis = (binding.Axis ?? new AxisCalibration()) with { Invert = _invertCheck.IsChecked == true, Deadzone = _deadzoneSlider.Value, Saturation = _saturationSlider.Value };
            _draftProfile = ProfileEditor.UpsertBinding(_draftProfile, binding with { Axis = axis });
            UpdateBindingList(); UpdateValidation();
        }
        UpdateCalibrationPreview(); UpdateCalibrationGraph();
    }

    private void AcceptCalibrationRange()
    {
        if (!_rangeReview || _calibrationCapture == null || _calibrationActive) { SetCalibrationStatus("Capture a range and choose Review range first."); return; }
        if (!TryGetCapturedCalibration(out var axis, out var problem))
        { SetCalibrationStatus(problem!); return; }
        var binding = GetCalibrationBinding();
        if (binding == null || _draftProfile == null || _selectedDevice == null) return;
        var candidate = ProfileEditor.UpsertBinding(_draftProfile, binding with { Axis = axis });
        var issues = ProfileEditor.ValidateProfile(candidate, _selectedDevice.MaxInputReportLength);
        if (issues.Count > 0) { SetCalibrationStatus(issues[0].Message); return; }
        _rangeReview = false;
        ApplyCalibration();
        _calibrationStage = CalibrationStage.Idle;
        _calibrationStep.Text = "Range accepted into draft. Save changes when ready.";
        UpdateCalibrationControls();
        SchedulePreview();
    }

    private void ResetCalibration()
    {
        if (GetCalibrationBinding() is not { } binding || !IsAnalogSource(binding) || _draftProfile == null) return;
        _rangeReview = false;
        ClearUiError("calibration", _selectedDevice?.Id);
        _draftProfile = ProfileEditor.UpsertBinding(_draftProfile, binding with { Axis = new AxisCalibration() });
        UpdateBindingList(); UpdateValidation(); LoadCalibrationFromTarget();
    }

    private void UpdateCalibrationGraph()
    {
        _calibrationOutputLabel.Text = "Draft output";
        _calibrationOutputLabel.Foreground = CopperTheme.Muted;
        _calibrationLegend.Text = "Gray: raw · Copper: adjusted · Shaded: deadzone";
        var binding = GetCalibrationBinding();
        if (binding == null || _lastReport == null || !SourceFitsReport(binding.Source, _lastReport))
        {
            _calibrationOutputLabel.Text = binding == null ? "No mapped source" : "No live input";
            _calibrationGraph.Update(0, 0, 0, ProfileEditor.IsTriggerTarget(CalibrationTarget));
            _calibrationValues.Text = binding == null ? "Assign this axis in Mapping to calibrate it." : "Waiting for live input. Reconnect or move the control.";
            return;
        }
        var raw = ReadCalibrationRaw(binding, _lastReport);
        if (!IsAnalogSource(binding))
        {
            var pressed = ProfileControllerMapper.ReadButtonSource(binding.Source, _lastReport, _lastReport.Length) ? 1d : 0d;
            _calibrationGraph.Update(pressed, pressed, 0, true);
            _calibrationValues.Text = $"Raw {raw} · Input {pressed:0.000} · Adjusted {pressed:0.000}";
            return;
        }
        var draftAxis = binding.Axis ?? new AxisCalibration();
        var axis = draftAxis;
        AxisCalibration? captured = null;
        var capturePreview = _calibrationStage == CalibrationStage.Review && TryGetCapturedCalibration(out captured, out _);
        if (capturePreview)
        {
            axis = captured!;
            _calibrationOutputLabel.Text = "Capture preview · Not applied";
            _calibrationOutputLabel.Foreground = CopperTheme.Warning;
            _calibrationLegend.Text = "Gray: raw · Copper: capture preview · Shaded: deadzone";
        }
        else if (_rangeReview || _calibrationActive)
        {
            _calibrationOutputLabel.Text = "Draft output · Capture not applied";
        }
        var trigger = ProfileEditor.IsTriggerTarget(CalibrationTarget);
        var input = trigger ? InputNormalization.NormalizeTrigger(raw, axis.Minimum, axis.Maximum, 0, 1) : InputNormalization.NormalizeAxis(raw, axis.Minimum, axis.Maximum, axis.Center, false, 0, 1);
        var output = CalibrationOutput(binding, raw, axis);
        _calibrationGraph.Update(input, output, axis.Deadzone, trigger, CalibrationTarget is ControllerElement.LeftStickY or ControllerElement.RightStickY);
        _calibrationValues.Text = $"Raw {raw}   ·   Input {input:0.000}   ·   {(capturePreview ? "Preview" : "Adjusted")} {output:0.000}" +
            (capturePreview ? $"\nCurrent draft output {CalibrationOutput(binding, raw, draftAxis):0.000}" : "");
    }

    private void ResetLiveControls()
    {
        _gamepad.SetState(null);
        _calibrationGraph.Update(0, 0, 0, false);
        _leftStick.SetPosition(0, 0); _rightStick.SetPosition(0, 0);
        _leftTrigger.Value = _rightTrigger.Value = 0;
        foreach (var target in _indicators.Keys) SetIndicator(target, false);
        _liveNumbers.Text = _stateText.Text = "Waiting for controller input.";
        _rawHexText.Text = _changedBytesText.Text = _reportRateText.Text = "";
    }
}
