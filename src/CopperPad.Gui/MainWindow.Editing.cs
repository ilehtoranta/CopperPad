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
    private readonly TextBox _profileName = new();
    private readonly Button _setupButton = new() { Content = "Set up controller", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _revertButton = new() { Content = "Revert" }, _undoButton = new() { Content = "Undo" }, _redoButton = new() { Content = "Redo" };
    private readonly Button _backGuided = new() { Content = "Back", IsEnabled = false }, _retryGuided = new() { Content = "Retry", IsEnabled = false };
    private readonly StackPanel _mappingRows = new() { Spacing = 6 };
    private readonly Expander _advancedEditor = new() { Header = "Advanced · Raw HID binding editor", HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly GamepadView _gamepad = new();
    private readonly CalibrationGraph _calibrationGraph = new();
    private readonly NumericUpDown _deadzoneNumber = new() { Minimum = 0, Maximum = .95m, Increment = .01m, FormatString = "0.00", Width = 110, ShowButtonSpinner = false };
    private readonly NumericUpDown _saturationNumber = new() { Minimum = .1m, Maximum = 1, Increment = .01m, FormatString = "0.00", Width = 110, ShowButtonSpinner = false };
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
        var other = _sessions.Values.Count(x => x != _session && x.IsDirty);
        _saveState.Text = (_session?.IsDirty == true ? "Unsaved changes" : _session == null ? "No controller selected" : "Saved · No pending changes") + (other > 0 ? $" · {other} other unsaved controller(s)" : "");
        _saveState.Foreground = _session?.IsDirty == true || other > 0 ? CopperTheme.Warning : CopperTheme.Muted;
        _startGuidedMappingButton.IsEnabled = _selectedDevice != null && !_guidedMappingActive;
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
                if (_session.Issues.Count > 0) { _previewText.Text = "Preview unchanged · Fix validation errors to preview these edits."; return; }
                _host.UpdateProfiles(_session.Preview(_profiles));
                _previewText.Text = "LIVE DRAFT PREVIEW · Changes are not saved";
            }
            else
            {
                _host.UpdateProfiles(_profiles);
                var mapping = _selectedDevice == null ? "" : MappingDisplay.Format(GetMappingInfo(_selectedDevice));
                _previewText.Text = mapping.Contains("Fallback", StringComparison.OrdinalIgnoreCase) ? "BUILT-IN OUTPUT · Generic fallback — unverified" : "SAVED / BUILT-IN OUTPUT";
            }
        }), TimeSpan.FromMilliseconds(200));
    }

    private async Task<bool> SaveSessionAsync(EditorSession session)
    {
        if (_saving) return false;
        if (!session.IsDirty) return true;
        if (session.Issues.Count != 0) { SetStatus($"Cannot save {session.Draft.Name}: {session.Issues[0].Message}"); return false; }
        var profile = session.Draft with { UpdatedAt = DateTimeOffset.UtcNow };
        var candidate = session.WithSavedDraft(_profiles, profile);
        _saving = true;
        IsEnabled = false;
        try
        {
            await _profileStore.SaveAsync(candidate);
            _profiles = candidate;
            session.MarkSaved(profile);
            if (session == _session) RestoreSession();
            SchedulePreview();
            SetStatus($"Saved {profile.Name}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetStatus($"Could not save {profile.Name}: {ex.Message}. Your draft is retained; retry saving.");
            return false;
        }
        finally { _saving = false; IsEnabled = true; }
    }

    private async Task<bool> ResolveDirtySessionsAsync(bool discardImmediately = true)
    {
        var dirty = _sessions.Values.Where(s => s.IsDirty).ToArray();
        if (dirty.Length == 0) return true;
        var choice = await AskAsync("Unsaved changes", $"You have changes for {dirty.Length} controller(s).", "Save all", "Discard", "Cancel");
        if (choice == "Save all")
        {
            foreach (var session in dirty) if (!await SaveSessionAsync(session)) return false;
            return true;
        }
        if (choice != "Discard") return false;
        if (discardImmediately)
        {
            foreach (var session in dirty) session.Revert();
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
        if (importedProfiles.Profiles.Any(p => ProfileEditor.ValidateProfile(p, int.MaxValue).Count > 0)) throw new InvalidOperationException("The imported document contains invalid profiles.");
        if (await AskAsync("Replace saved profiles?", $"This replaces {_profiles.Profiles.Count} saved profiles with {importedProfiles.Profiles.Count} imported profiles.", "Replace", "Cancel") != "Replace") return;
        if (!await ResolveDirtySessionsAsync(discardImmediately: false)) return;
        await _profileStore.SaveAsync(importedProfiles);
        _profiles = importedProfiles;
        _sessions.Clear();
        _session = null;
        var selected = _selectedDevice;
        _selectedDevice = null;
        SelectDevice(selected);
        SetStatus("Imported profiles.");
    }

    private void RestartGuidedTarget(int index)
    {
        if (!_guidedMappingActive) return;
        _guidedTargetIndex = index;
        _guidedIgnoredSourceKeys.Clear();
        BeginGuidedArming();
        _guidedProgress.Text = $"Control {index + 1} of {ProfileEditor.MappableTargets.Count} · {Friendly(ProfileEditor.MappableTargets[index])}";
        _guidedPromptText.BringIntoView();
    }

    private ControllerElement CalibrationTarget => _calibrationTargetBox.SelectedItem is MappingTargetItem item ? item.Element : ControllerElement.LeftStickX;

    private void CalibrationSettingChanged()
    {
        if (_loadingCalibration) return;
        _loadingCalibration = true;
        _deadzoneNumber.Value = (decimal)_deadzoneSlider.Value;
        _saturationNumber.Value = (decimal)_saturationSlider.Value;
        _loadingCalibration = false;
        if (!_rangeReview && !_calibrationActive && GetCalibrationBinding() is { } binding && _draftProfile != null)
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
        if (_calibrationCapture.Minimum == null || _calibrationCapture.Maximum == null || _calibrationCapture.Maximum <= _calibrationCapture.Minimum)
        { SetCalibrationStatus("Move the control through a non-zero range before accepting."); return; }
        var binding = GetCalibrationBinding();
        if (binding == null || _draftProfile == null || _selectedDevice == null) return;
        var axis = _calibrationCapture.ToCalibration(_invertCheck.IsChecked == true, _deadzoneSlider.Value, _saturationSlider.Value);
        var candidate = ProfileEditor.UpsertBinding(_draftProfile, binding with { Axis = axis });
        var issues = ProfileEditor.ValidateProfile(candidate, _selectedDevice.MaxInputReportLength);
        if (issues.Count > 0) { SetCalibrationStatus(issues[0].Message); return; }
        _rangeReview = false;
        ApplyCalibration();
        _calibrationStep.Text = "Range accepted into draft. Save changes when ready.";
        SchedulePreview();
    }

    private void ResetCalibration()
    {
        if (GetCalibrationBinding() is not { } binding || _draftProfile == null) return;
        _rangeReview = false;
        _draftProfile = ProfileEditor.UpsertBinding(_draftProfile, binding with { Axis = new AxisCalibration() });
        UpdateBindingList(); UpdateValidation(); LoadCalibrationFromTarget();
    }

    private void UpdateCalibrationGraph()
    {
        var binding = GetCalibrationBinding();
        if (binding == null || _lastReport == null) { _calibrationValues.Text = "Assign this axis in Mapping to enable live calibration."; return; }
        var raw = ReportAnalyzer.ReadSourceValue(binding.Source, _lastReport);
        var axis = _rangeReview && _calibrationCapture != null ? _calibrationCapture.ToCalibration(_invertCheck.IsChecked == true, _deadzoneSlider.Value, _saturationSlider.Value) : binding.Axis ?? new AxisCalibration();
        var trigger = ProfileEditor.IsTriggerTarget(CalibrationTarget);
        var input = trigger ? InputNormalization.NormalizeTrigger(raw, axis.Minimum, axis.Maximum, 0, 1) : InputNormalization.NormalizeAxis(raw, axis.Minimum, axis.Maximum, axis.Center, false, 0, 1);
        var output = trigger ? InputNormalization.NormalizeTrigger(raw, axis.Minimum, axis.Maximum, axis.Deadzone, axis.Saturation) : InputNormalization.NormalizeAxis(raw, axis.Minimum, axis.Maximum, axis.Center, axis.Invert, axis.Deadzone, axis.Saturation);
        _calibrationGraph.Update(input, output, axis.Deadzone, trigger, CalibrationTarget is ControllerElement.LeftStickY or ControllerElement.RightStickY);
        _calibrationValues.Text = $"Raw {raw}   ·   Input {input:0.000}   ·   Adjusted {output:0.000}";
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
