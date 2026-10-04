using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CopperPad;

namespace CopperPad.Gui;

internal sealed partial class MainWindow : Window, IDisposable
{
	private static readonly TimeSpan RawReportUiInterval = TimeSpan.FromMilliseconds(50);
	private static readonly TimeSpan ReportTextUpdateInterval = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan CaptureStatusUpdateInterval = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan ExplicitCaptureStatusHold = TimeSpan.FromSeconds(1.5);
	private static readonly TimeSpan GuidedMappingArmDelay = TimeSpan.FromMilliseconds(900);
	private static readonly TimeSpan GuidedMappingReadyDelay = TimeSpan.FromMilliseconds(900);
	private static readonly TimeSpan GuidedMappingLockDelay = TimeSpan.FromSeconds(1);
	private static readonly ControllerElement[] AxisTargets =
	[
		ControllerElement.LeftStickX,
		ControllerElement.LeftStickY,
		ControllerElement.RightStickX,
		ControllerElement.RightStickY,
		ControllerElement.LeftTrigger,
		ControllerElement.RightTrigger
	];

	private readonly FileControllerProfileStore _profileStore = new(CopperPadProfilePaths.GetDefaultProfilePath());
	private readonly IControllerGuiService _host;
	private readonly ListBox _deviceList = new();
	private readonly TextBlock _statusText = TextBlock();
	private readonly TextBlock _deviceFilterText = TextBlock();
	private readonly CheckBox _showAllDevicesCheck = new() { Content = "Show all HID" };



	private readonly TabControl _tabs = new();
	private readonly TextBlock _stateText = TextBlock();
	private readonly TextBlock _rawHexText = MonospaceTextBlock();
	private readonly TextBlock _changedBytesText = TextBlock();
	private readonly TextBlock _descriptorText = MonospaceTextBlock();
	private readonly TextBlock _reportRateText = TextBlock();
	private readonly TextBlock _captureStatusText = TextBlock();
	private readonly TextBlock _guidedPromptText = TextBlock();
	private readonly TextBlock _validationText = TextBlock();
	private readonly TextBlock _calibrationStatusText = TextBlock();
	private readonly TextBlock _calibrationPreviewText = TextBlock();
	private readonly StickView _leftStick = new();
	private readonly StickView _rightStick = new();
	private readonly ProgressBar _leftTrigger = new() { Minimum = 0, Maximum = 1, Height = 16 };
	private readonly ProgressBar _rightTrigger = new() { Minimum = 0, Maximum = 1, Height = 16 };
	private readonly Dictionary<ControllerElement, Border> _indicators = new();
	private readonly ComboBox _targetBox = new() { MinWidth = 220 };
	private readonly ComboBox _sourceKindBox = new() { MinWidth = 220 };
	private readonly NumericUpDown _offsetBox = NumberBox(0, 512);
	private readonly NumericUpDown _bitBox = NumberBox(0, 7);
	private readonly NumericUpDown _hatBox = NumberBox(0, 15);
	private readonly CheckBox _sourceInvertCheck = new() { Content = "Active low / inverted" };

	private readonly Button _startGuidedMappingButton = new() { Content = "Start Guided Mapping" };
	private readonly Button _skipGuidedMappingButton = new() { Content = "Skip", IsEnabled = false };
	private readonly Button _stopGuidedMappingButton = new() { Content = "Stop", IsEnabled = false };
	private readonly Button _useSuggestionButton = new() { Content = "Use Change", IsEnabled = false };
	private readonly Button _ignoreSuggestionButton = new() { Content = "Ignore Change", IsEnabled = false };
	private readonly Button _saveProfileButton = new() { Content = "Save Profile" };

	private readonly ComboBox _calibrationTargetBox = new() { MinWidth = 170 };
	private readonly CheckBox _invertCheck = new() { Content = "Invert" };
	private readonly Slider _deadzoneSlider = new() { Minimum = 0, Maximum = 0.95, Value = 0.1, Width = 180 };
	private readonly Slider _saturationSlider = new() { Minimum = 0.01, Maximum = 1, Value = 1, Width = 180 };
	private readonly object _rawReportGate = new();
	private ControllerProfileSet _profiles = ControllerProfileSet.Empty;
	private IReadOnlyList<HidDeviceInfo> _allDevices = Array.Empty<HidDeviceInfo>();
	private ControllerProfile? _draftProfile;
	private HidDeviceInfo? _selectedDevice;
	private PendingRawReport? _pendingRawReport;
	private byte[]? _lastReport;
	private byte[]? _previousReport;
	private byte[]? _baselineReport;
	private byte[]? _guidedReleaseBaselineReport;
	private ControllerBindingSource? _suggestedSource;
	private ControllerBindingSource? _guidedReleaseSource;
	private readonly GuidedMappingCapture _guidedCapture = new(GuidedMappingLockDelay);
	private readonly HashSet<string> _guidedIgnoredSourceKeys = new(StringComparer.Ordinal);
	private readonly Queue<DateTimeOffset> _reportTimes = new();
	private AxisCalibrationCapture? _calibrationCapture;
	private DateTimeOffset _nextRawReportUiUpdate = DateTimeOffset.MinValue;
	private DateTimeOffset _lastReportTextUpdate = DateTimeOffset.MinValue;
	private DateTimeOffset _lastCaptureStatusUpdate = DateTimeOffset.MinValue;
	private DateTimeOffset _captureStatusHoldUntil = DateTimeOffset.MinValue;
	private bool _rawReportDispatchScheduled;
	private bool _guidedMappingActive;
	private bool _guidedSingleTarget;
	private bool _deviceConnected;
    private bool _updatingDeviceList;
	private bool _guidedArming;
	private bool _guidedWaitingForNeutral;
	private bool _guidedReadyPromptShown;
	private bool _guidedLockCheckScheduled;
	private int _guidedTargetIndex;
	private DateTimeOffset _guidedArmUntil;
	private DateTimeOffset _guidedAcceptInputAt;
	private bool _calibrationActive;
	private bool _disposed;

	public MainWindow() : this(new ControllerGuiService()) { }

	internal MainWindow(IControllerGuiService host, FileControllerProfileStore? store = null)
	{
		_host = host;
		if (store != null) _profileStore = store;
		Title = "CopperPad";
		Width = 1180;
		Height = 760;
		MinWidth = 900;
		MinHeight = 600;
		Content = BuildContent();
		UpdateEditorState();
		UpdateSelectedDeviceDetails();
		Closing += OnWindowClosing;
		KeyDown += OnEditorKeyDown;

		_host.DevicesChanged += (_, args) => PostUi("Device refresh failed", () => OnDevicesChanged(args));
		_host.RawReportReceived += (_, args) => QueueRawReport(args);
		_host.SnapshotChanged += (_, args) => PostUi("Controller snapshot update failed", () => OnSnapshotChanged(args.Snapshot));
		Opened += async (_, _) => await TryRunUiActionAsync("Startup failed", InitializeAsync).ConfigureAwait(true);
		Closed += (_, _) => Dispose();
	}

	private async Task InitializeAsync()
	{
		try
		{
			_profiles = await _profileStore.LoadAsync().ConfigureAwait(true);
			_host.UpdateProfiles(_profiles);
			ClearUiError("load");
			SetStatus("");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
		{
			_profiles = ControllerProfileSet.Empty;
			SetStatus("Profile load failed: " + ex.Message);
			ShowUiError("load", null, "Saved profiles could not be loaded", "Check the profile document in Diagnostics or import a valid document.", ex.Message + "\nProfile document: " + _profileStore.Path, "Open Diagnostics", () => { _tabs.SelectedIndex = 3; return Task.CompletedTask; });
		}

		StartHost();
	}

	private void RefreshDevices()
	{
		try
		{
			_host.Stop();
			_host.Start();
			ClearUiError("devices");
		}
		catch (Exception ex) when (IsRecoverableHidException(ex))
		{
			SetStatus("Controller refresh failed: " + ex.Message);
			ShowDeviceFailure(ex);
		}
	}

	private void OnDevicesChanged(HidDevicesChangedEventArgs args)
	{
		_allDevices = args.Devices;
        foreach (var session in _sessions.Values)
        {
            var device = args.Devices.FirstOrDefault(d => d.Id == session.Device.Id);
            session.Connected = device != null;
            if (device != null) session.Device = device;
        }
		if (!string.IsNullOrWhiteSpace(args.Diagnostic))
		{
			SetStatus(args.Diagnostic);
			ShowUiError("devices", null, "Device scan needs attention", args.Diagnostic, args.Diagnostic, "Refresh devices", () => { RefreshDevices(); return Task.CompletedTask; });
		}
		else ClearUiError("devices");

		ApplyDeviceFilter();
	}

	private void ApplyDeviceFilter()
    {
        var selectedId = _selectedDevice?.Id;
        var showAll = _showAllDevicesCheck.IsChecked == true;
        var devices = showAll ? _allDevices : _allDevices.Where(DeviceDisplay.IsLikelyGameController);
        // Editing history must remain reachable even after Undo returns an offline draft to its baseline.
        var retained = _sessions.Values.Where(x => x.IsDirty || x.CanUndo || x.CanRedo).Select(x => x.Device);
        var items = devices.Concat(retained).DistinctBy(x => x.Id)
            .Select(device => new DeviceListItem(device, IsDeviceConnected(device))).ToArray();
        var visibleIds = items.Select(x => x.Device.Id).ToHashSet(StringComparer.Ordinal);
        var hiddenCount = _allDevices.Count(x => !visibleIds.Contains(x.Id));
        var connectedCount = items.Count(x => x.Connected);
        var offlineCount = items.Count(x => !x.Connected);
        _deviceFilterText.Text = $"{connectedCount} connected" + (offlineCount > 0 ? $" · {offlineCount} offline" : "") + (hiddenCount > 0 ? $" · {hiddenCount} hidden HID" : "");
        var selected = items.FirstOrDefault(x => x.Device.Id == selectedId) ?? items.FirstOrDefault();
        // Replacing ItemsSource raises selection events; restore the intended selection just once.
        _updatingDeviceList = true;
        try { _deviceList.ItemsSource = items; _deviceList.SelectedItem = selected; }
        finally { _updatingDeviceList = false; }
        SelectDevice(selected?.Device);
    }

    private bool IsDeviceConnected(HidDeviceInfo device) => _allDevices.Any(x => x.Id == device.Id) &&
        (!_sessions.TryGetValue(device.Id, out var session) || session.Connected);

	private void StartHost()
	{
		try
		{
			_host.Start();
			ClearUiError("devices");
		}
		catch (Exception ex) when (IsRecoverableHidException(ex))
		{
			SetStatus("Controller scan failed: " + ex.Message);
			ShowDeviceFailure(ex);
		}
	}

	private void SelectDevice(HidDeviceInfo? device)
	{
		var connected = device != null && IsDeviceConnected(device);
        if (_selectedDevice?.Id == device?.Id && device != null && _session != null && connected == _deviceConnected)
        {
            _selectedDevice = device;
            if (_session != null) { _session.Device = device; _session.Connected = connected; }
            UpdateSelectedDeviceDetails();
            return;
        }
		if (_session != null) _session.SelectedPage = _tabs.SelectedIndex;
		_session = null;
		_selectedDevice = device;
        _deviceConnected = connected;
		_rangeReview = false;
		_calibrationActive = false;
		_previewVersion++;
		ResetLiveControls();
		_lastReport = null;
		_previousReport = null;
        lock (_rawReportGate) _pendingRawReport = null;
		_baselineReport = null;
		_guidedReleaseBaselineReport = null;
		_suggestedSource = null;
		_useSuggestionButton.IsEnabled = false;
		_ignoreSuggestionButton.IsEnabled = false;
		_guidedReleaseSource = null;
		_reportTimes.Clear();
		_lastReportTextUpdate = DateTimeOffset.MinValue;
		_lastCaptureStatusUpdate = DateTimeOffset.MinValue;
		_captureStatusHoldUntil = DateTimeOffset.MinValue;
		_guidedMappingActive = false;
        _guidedSingleTarget = false;
		_guidedArming = false;
		_guidedWaitingForNeutral = false;
		_guidedReadyPromptShown = false;
		_guidedLockCheckScheduled = false;
		_guidedCapture.Reset();
		_guidedIgnoredSourceKeys.Clear();
		_guidedReleaseSource = null;
		_guidedReleaseBaselineReport = null;
		UpdateGuidedButtons();
		_guidedPromptText.Text = device == null ? "Connect a controller to map its controls." : connected ? "Use guided setup, or remap one control below." : "Reconnect this controller to capture input. You can still edit or save its draft.";
        _guidedPromptText.Foreground = CopperTheme.Muted;
		if (device == null)
		{
			_descriptorText.Text = "";

			_draftProfile = null;
			_saveProfileButton.Content = "Save Profile";
			_saveProfileButton.IsVisible = false;



			ShowSummaryWorkspace();
			SetStatus("");
		}
		else
		{
			if (!_sessions.TryGetValue(device.Id, out _session))
			{
				_session = new EditorSession(device, FindSavedProfile(device));
				_sessions.Add(device.Id, _session);
			}
			_session.Device = device;
            _session.Connected = connected;
			_draftProfile = _session.Draft;

			UpdateSelectedDeviceDetails();
			ShowSummaryWorkspace();
			SetStatus("");
		}

		UpdateBindingList();
		UpdateValidation();
		LoadCalibrationFromTarget();
		UpdateCaptureStatus();
        // Runtime failures must not interrupt restoring a session or its editing controls.
        TryRunUiAction("Preview failed", () => _host.UpdateProfiles(_profiles));
        TryRunUiAction("Controller connection failed", () => _host.SelectDevice(connected ? device?.Id : null));
	}

	private void QueueRawReport(ControllerRawReportReceivedEventArgs args)
	{
		TimeSpan delay;
		lock (_rawReportGate)
		{
			_pendingRawReport = new PendingRawReport(args.Device, args.Report.ToArray(), args.Timestamp);
            if (_rangeCaptureSource is { } capture && capture.DeviceId == args.Device.Id && SourceFitsReport(capture.Binding.Source, _pendingRawReport.Report))
            {
                var raw = ReadCalibrationRaw(capture.Binding, _pendingRawReport.Report);
                _pendingRangeMinimum = _pendingRangeMinimum.HasValue ? Math.Min(_pendingRangeMinimum.Value, raw) : raw;
                _pendingRangeMaximum = _pendingRangeMaximum.HasValue ? Math.Max(_pendingRangeMaximum.Value, raw) : raw;
                _pendingRangeLast = raw;
            }
			if (_rawReportDispatchScheduled)
			{
				return;
			}

			_rawReportDispatchScheduled = true;
			var now = DateTimeOffset.UtcNow;
			delay = _nextRawReportUiUpdate > now ? _nextRawReportUiUpdate - now : TimeSpan.Zero;
		}

		Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(ProcessPendingRawReport, delay));
	}

	private void ProcessPendingRawReport()
	{
		PendingRawReport? pending;
		lock (_rawReportGate)
		{
			pending = _pendingRawReport;
			_pendingRawReport = null;
			_rawReportDispatchScheduled = false;
			_nextRawReportUiUpdate = DateTimeOffset.UtcNow + RawReportUiInterval;
		}

		if (pending != null)
		{
			TryRunUiAction("Raw report update failed", () => OnRawReport(pending));
		}
	}

	private void OnRawReport(PendingRawReport pending)
	{
		if (!_deviceConnected || _selectedDevice == null || !string.Equals(pending.Device.Id, _selectedDevice.Id, StringComparison.Ordinal))
		{
			return;
		}

		_previousReport = _lastReport;
		_lastReport = pending.Report;
		_reportTimes.Enqueue(pending.Timestamp);
		while (_reportTimes.Count > 0 && pending.Timestamp - _reportTimes.Peek() > TimeSpan.FromSeconds(1))
		{
			_reportTimes.Dequeue();
		}

		if (!_diagnosticsPaused && (_lastReportTextUpdate == DateTimeOffset.MinValue ||
			pending.Timestamp - _lastReportTextUpdate >= ReportTextUpdateInterval))
		{
			_lastReportTextUpdate = pending.Timestamp;
			_rawHexText.Text = ToHexRows(_lastReport);
			_reportRateText.Text = $"Reports: {_reportTimes.Count}/s   Last: {pending.Timestamp:HH:mm:ss.fff}";
			var changes = ReportAnalyzer.DetectChanges(_previousReport, _lastReport);
			_changedBytesText.Text = changes.Count == 0
				? "Changed bytes: none"
				: "Changed bytes: " + string.Join(", ", changes.Take(10).Select(change => $"{change.Offset}: {change.Baseline:X2}->{change.Current:X2}"));
		}

		if (_baselineReport != null)
		{
			var baselineChanges = ReportAnalyzer.DetectChanges(_baselineReport, _lastReport);
			var neutralChanges = _previousReport == null
				? Array.Empty<ReportChange>()
				: ReportAnalyzer.DetectChanges(_previousReport, _lastReport);
			UpdateGuidedMapping(baselineChanges, neutralChanges, pending.Timestamp);
			var selectedTarget = GetSelectedTarget();
			var best = SelectBestSuggestedChange(selectedTarget, baselineChanges);
			if (best != null)
			{
				_suggestedSource = best.SuggestedSource;
				_useSuggestionButton.IsEnabled = true;
				_ignoreSuggestionButton.IsEnabled = true;
			}
			else
			{
				_suggestedSource = null;
				_useSuggestionButton.IsEnabled = false;
				_ignoreSuggestionButton.IsEnabled = false;
			}

			UpdateCaptureStatus(force: false);
		}

		DrainCalibrationReports();
		UpdateCalibrationGraph();
        UpdateCalibrationControls();
	}

	private void OnSnapshotChanged(CopperControllerSnapshot state)
	{
		if (_selectedDevice == null || !string.Equals(state.ControllerId, _selectedDevice.Id, StringComparison.Ordinal))
		{
			return;
		}
        // Reports queued before an enumeration disconnect must not revive an offline editor.
        if (state.IsConnected && !_allDevices.Any(x => x.Id == state.ControllerId)) return;

        var connectionChanged = _deviceConnected != state.IsConnected;
        _deviceConnected = state.IsConnected;
        if (_session != null) _session.Connected = state.IsConnected;
		if (!state.IsConnected)
        {
            ResetLiveControls();
            _lastReport = null;
            lock (_rawReportGate) _pendingRawReport = null;
            if (_guidedMappingActive) StopGuidedMapping("Controller disconnected. Completed assignments retained; reconnect to continue.");
            else _guidedPromptText.Text = "Reconnect this controller to capture input. You can still edit or save its draft.";
            if (_rangeReview || _calibrationActive) CancelCalibration();
            UpdateConnectionBadge(false);
            _liveNumbers.Text = _stateText.Text = "Controller disconnected. Reconnect to test input.";
            UpdateCalibrationControls();
            UpdateCalibrationGraph();
            if (connectionChanged)
            {
                TryRunUiAction("Controller connection failed", () => _host.SelectDevice(null));
                ApplyDeviceFilter();
            }
            if (!string.IsNullOrWhiteSpace(state.Diagnostic)) ShowInputFailure(state.ControllerId, state.Diagnostic);
            return;
        }
        if (connectionChanged) ApplyDeviceFilter();
        UpdateConnectionBadge(true);
		_gamepad.SetState(state);
		var leftX = state.GetAxis(ControllerElement.LeftStickX);
		var leftY = state.GetAxis(ControllerElement.LeftStickY);
		var rightX = state.GetAxis(ControllerElement.RightStickX);
		var rightY = state.GetAxis(ControllerElement.RightStickY);
		var leftTrigger = state.GetAxis(ControllerElement.LeftTrigger);
		var rightTrigger = state.GetAxis(ControllerElement.RightTrigger);
		_leftStick.SetPosition(leftX, leftY);
		_rightStick.SetPosition(rightX, rightY);
		_leftTrigger.Value = leftTrigger;
		_rightTrigger.Value = rightTrigger;
		SetIndicator(ControllerElement.A, state.IsPressed(ControllerElement.A));
		SetIndicator(ControllerElement.B, state.IsPressed(ControllerElement.B));
		SetIndicator(ControllerElement.X, state.IsPressed(ControllerElement.X));
		SetIndicator(ControllerElement.Y, state.IsPressed(ControllerElement.Y));
		SetIndicator(ControllerElement.LeftShoulder, state.IsPressed(ControllerElement.LeftShoulder));
		SetIndicator(ControllerElement.RightShoulder, state.IsPressed(ControllerElement.RightShoulder));
		SetIndicator(ControllerElement.Select, state.IsPressed(ControllerElement.Select));
		SetIndicator(ControllerElement.Start, state.IsPressed(ControllerElement.Start));
		SetIndicator(ControllerElement.Menu, state.IsPressed(ControllerElement.Menu));
		SetIndicator(ControllerElement.LeftStickButton, state.IsPressed(ControllerElement.LeftStickButton));
		SetIndicator(ControllerElement.RightStickButton, state.IsPressed(ControllerElement.RightStickButton));
		SetIndicator(ControllerElement.DPadUp, state.IsPressed(ControllerElement.DPadUp));
		SetIndicator(ControllerElement.DPadDown, state.IsPressed(ControllerElement.DPadDown));
		SetIndicator(ControllerElement.DPadLeft, state.IsPressed(ControllerElement.DPadLeft));
		SetIndicator(ControllerElement.DPadRight, state.IsPressed(ControllerElement.DPadRight));
		_stateText.Text =
			$"LX {leftX:0.00}  LY {leftY:0.00}  RX {rightX:0.00}  RY {rightY:0.00}  LT {leftTrigger:0.00}  RT {rightTrigger:0.00}";
		_liveNumbers.Text = _stateText.Text;
		if (!string.IsNullOrWhiteSpace(state.Diagnostic))
		{
			SetStatus(state.Diagnostic);
            if (state.MappingSource != ControllerMappingSource.None) ShowInputFailure(state.ControllerId, state.Diagnostic);
		}
        else ClearUiError("input", state.ControllerId);
	}

	private void CaptureBaseline()
	{
		_baselineReport = _lastReport?.ToArray();
		_suggestedSource = null;
		_useSuggestionButton.IsEnabled = false;
		_ignoreSuggestionButton.IsEnabled = false;
		if (_baselineReport == null)
		{
			SetCaptureStatus("No report received yet. Move or press the controller once, then capture baseline again.", ExplicitCaptureStatusHold);
			ShowCaptureError("No report received. Move or press the controller once, then capture a baseline in Advanced.", true);
			return;
		}

		SetCaptureStatus($"Baseline captured: {_baselineReport.Length} bytes. Press or move one physical control.", ExplicitCaptureStatusHold);
		ClearUiError("capture", _selectedDevice?.Id);
	}

	private void AddOrUpdateBinding()
	{
		if (_draftProfile == null)
		{
			return;
		}

		var target = GetSelectedTarget();
		var existingAxis = _draftProfile.Bindings.FirstOrDefault(binding => binding.Target == target)?.Axis;
		var binding = ProfileEditor.CreateBinding(target, GetSourceFromFields(), existingAxis);
		var candidate = ProfileEditor.UpsertBinding(_draftProfile, binding);
		var issues = ProfileEditor.ValidateProfile(candidate, _selectedDevice?.MaxInputReportLength ?? 0);
		if (issues.Count > 0)
        {
            _assignmentError.Text = string.Join("\n", issues.Select(x => x.Message));
            _assignmentError.IsVisible = true;
            var deviceId = _selectedDevice?.Id;
            var source = binding.Source;
            ShowUiError("assignment", deviceId, "Input was not assigned", $"{_selectedDevice?.ProductName} · {Friendly(target)}: {issues[0].Message}", _assignmentError.Text, "Fix assignment", () => {
                NavigateToError(deviceId, 1); SetSelectedTarget(target); SetSourceFields(source);
                _advancedEditor.IsExpanded = true; RevealErrorControl(_offsetBox); return Task.CompletedTask;
            });
            SetCaptureStatus("Assignment needs correction. See the error panel.", ExplicitCaptureStatusHold);
            return;
        }
        _assignmentError.IsVisible = false;
		ClearUiError("assignment", _selectedDevice?.Id);
		_draftProfile = candidate;
		UpdateBindingList();
		UpdateValidation();
		LoadCalibrationFromTarget();
		SetCaptureStatus($"Added / updated {target}: {ReportAnalyzer.FormatSource(binding.Source)}", ExplicitCaptureStatusHold);
	}

    private void StartGuidedMapping()
    {
        var firstMissing = ProfileEditor.MappableTargets.Select((target, index) => new { target, index })
            .FirstOrDefault(item => _draftProfile?.Bindings.Any(binding => binding.Target == item.target) != true);
        BeginGuidedMapping(firstMissing?.index ?? 0, singleTarget: false);
    }

    private void RemapControl(ControllerElement target)
    {
        var index = ProfileEditor.MappableTargets.ToList().IndexOf(target);
        if (index >= 0) BeginGuidedMapping(index, singleTarget: true);
    }

    private void BeginGuidedMapping(int targetIndex, bool singleTarget)
    {
        if (_guidedMappingActive) return;
        if (_draftProfile == null) { ShowCaptureError("Select a controller to begin mapping."); return; }
        if (!_deviceConnected) { ShowCaptureError("Reconnect this controller before capturing input."); return; }
        if (_lastReport == null) { ShowCaptureError("Waiting for reports. Release the controls and press a button once, then try again."); return; }
        ClearUiError("capture", _selectedDevice?.Id);
        _guidedPromptText.Foreground = CopperTheme.Copper;
        _guidedSingleTarget = singleTarget;
        _guidedTargetIndex = targetIndex;
        _guidedMappingActive = true;
        _guidedIgnoredSourceKeys.Clear();
        _suggestedSource = null;
        _useSuggestionButton.IsEnabled = _ignoreSuggestionButton.IsEnabled = false;
        BeginGuidedArming();
    }

	private void IgnoreSuggestedSource()
	{
		if (_suggestedSource == null)
		{
			return;
		}

		var ignored = ReportAnalyzer.FormatSource(_suggestedSource);
		AddGuidedIgnoredSource(_suggestedSource);
		_suggestedSource = null;
		_useSuggestionButton.IsEnabled = false;
		_ignoreSuggestionButton.IsEnabled = false;
		_guidedCapture.Reset();
		SetCaptureStatus("Ignoring " + ignored + " for this mapping session.", ExplicitCaptureStatusHold);
	}

	private void SkipGuidedTarget()
	{
		if (!_guidedMappingActive)
		{
			return;
		}

        if (_guidedSingleTarget && !_guidedWaitingForNeutral)
        {
            StopGuidedMapping("Remap skipped. Existing assignments are unchanged.");
            return;
        }

		AdvanceGuidedTarget();
	}

	private void StopGuidedMapping(string message)
	{
		_guidedMappingActive = false;
        _guidedSingleTarget = false;
		_guidedArming = false;
		_guidedWaitingForNeutral = false;
		_guidedReadyPromptShown = false;
		_guidedLockCheckScheduled = false;
		_guidedCapture.Reset();
		_guidedIgnoredSourceKeys.Clear();
		_guidedReleaseSource = null;
		_guidedReleaseBaselineReport = null;
		_suggestedSource = null;
		_useSuggestionButton.IsEnabled = false;
		_ignoreSuggestionButton.IsEnabled = false;
		UpdateGuidedButtons();
		_gamepad.Highlight = null;
        _guidedProgress.Text = "";
        RenderMappingRows();
		_guidedPromptText.Text = message;
        _guidedPromptText.Foreground = CopperTheme.Muted;
		SchedulePreview();
		SetCaptureStatus(message, ExplicitCaptureStatusHold);
	}

	private void UpdateGuidedMapping(
		IReadOnlyList<ReportChange> changes,
		IReadOnlyList<ReportChange> neutralChanges,
		DateTimeOffset timestamp)
	{
		if (!_guidedMappingActive || _baselineReport == null || _lastReport == null)
		{
			return;
		}

		if (_guidedArming)
		{
			AddGuidedIgnoredChanges(changes);
			AddGuidedIgnoredChanges(neutralChanges);

			if (timestamp >= _guidedArmUntil)
			{
				_guidedArming = false;
				_guidedReadyPromptShown = false;
				_guidedAcceptInputAt = timestamp + GuidedMappingReadyDelay;
				_guidedCapture.Reset();
				ShowGuidedPrompt();
			}

			return;
		}

		if (_guidedWaitingForNeutral)
		{
			if (_guidedReleaseSource != null &&
				_guidedReleaseBaselineReport != null &&
				GuidedMappingCapture.IsSourceReleased(_guidedReleaseSource, _guidedReleaseBaselineReport, _lastReport))
			{
				AdvanceGuidedTarget();
			}

			return;
		}

		if (timestamp < _guidedAcceptInputAt)
		{
			AddGuidedIgnoredChanges(changes);
			AddGuidedIgnoredChanges(neutralChanges);

			return;
		}

		if (!_guidedReadyPromptShown)
		{
			AddGuidedIgnoredChanges(changes);
			AddGuidedIgnoredChanges(neutralChanges);

			_baselineReport = _lastReport.ToArray();
			_guidedCapture.Reset();
			_guidedReadyPromptShown = true;
			ShowGuidedPrompt();
			return;
		}

		var target = ProfileEditor.MappableTargets[_guidedTargetIndex];
		var candidate = SelectGuidedCandidate(target, changes);
		if (!_guidedCapture.Observe(target, candidate, _baselineReport, _lastReport, timestamp))
		{
			if (_guidedCapture.CandidateSource != null)
			{
				ScheduleGuidedLockCheck();
			}

			return;
		}

		CommitGuidedLockedSource();
	}

	private ReportChange? SelectGuidedCandidate(ControllerElement target, IReadOnlyList<ReportChange> changes)
	{
		if (_baselineReport == null || _lastReport == null)
		{
			return null;
		}

		var bestScore = -1;
		ReportChange? best = null;
		foreach (var change in changes)
		{
			var normalized = GuidedMappingCapture.NormalizeForTarget(target, change, _baselineReport, _lastReport);
			var source = normalized.SuggestedSource;
			if (IsGuidedSourceIgnored(source) ||
				IsGuidedSourceAlreadyBound(target, source))
			{
				continue;
			}

			var score = GuidedMappingCapture.GetCandidateScore(target, normalized, _baselineReport, _lastReport) +
				ProfileEditor.GetSourcePreferenceScore(_draftProfile, target, source);
			if (score > bestScore)
			{
				bestScore = score;
				best = normalized;
			}
		}

		return best;
	}

	private ReportChange? SelectBestSuggestedChange(ControllerElement target, IReadOnlyList<ReportChange> changes)
	{
		if (_baselineReport == null || _lastReport == null)
		{
			return null;
		}

		var bestScore = int.MinValue;
		ReportChange? best = null;
		foreach (var change in changes)
		{
			var normalized = GuidedMappingCapture.NormalizeForTarget(target, change, _baselineReport, _lastReport);
			var source = normalized.SuggestedSource;
			if (IsGuidedSourceIgnored(source))
			{
				continue;
			}

			var score = GuidedMappingCapture.GetCandidateScore(target, normalized, _baselineReport, _lastReport) +
				ProfileEditor.GetSourcePreferenceScore(_draftProfile, target, source);
			if (score > bestScore)
			{
				bestScore = score;
				best = normalized;
			}
		}

		return bestScore >= 0 ? best : null;
	}

	private void AddGuidedIgnoredChanges(IReadOnlyList<ReportChange> changes)
	{
		foreach (var change in changes)
		{
			AddGuidedIgnoredSource(change.SuggestedSource);
		}
	}

	private void AddGuidedIgnoredSource(ControllerBindingSource source)
	{
		_guidedIgnoredSourceKeys.Add(ProfileEditor.GetSourceKey(source));
		if (source.Kind == ControllerBindingSourceKind.ReportBit)
		{
			_guidedIgnoredSourceKeys.Add(ProfileEditor.GetSourceKey(source with { Invert = !source.Invert }));
		}
	}

	private bool IsGuidedSourceIgnored(ControllerBindingSource source)
	{
		if (_guidedIgnoredSourceKeys.Contains(ProfileEditor.GetSourceKey(source)))
		{
			return true;
		}

		return source.Kind == ControllerBindingSourceKind.ReportBit &&
			_guidedIgnoredSourceKeys.Contains(ProfileEditor.GetSourceKey(source with { Invert = !source.Invert }));
	}

	private void ScheduleGuidedLockCheck()
	{
		if (_guidedLockCheckScheduled)
		{
			return;
		}

		_guidedLockCheckScheduled = true;
		DispatcherTimer.RunOnce(() =>
			TryRunUiAction("Guided mapping lock failed", () =>
			{
				_guidedLockCheckScheduled = false;
				if (!_guidedMappingActive ||
					_guidedArming ||
					_guidedWaitingForNeutral ||
					_baselineReport == null ||
					_lastReport == null)
				{
					return;
				}

				if (_guidedCapture.TryLockCandidate(_baselineReport, _lastReport, DateTimeOffset.UtcNow))
				{
					CommitGuidedLockedSource();
				}
			}),
			GuidedMappingLockDelay);
	}

	private void CommitGuidedLockedSource()
	{
		if (_guidedCapture.LockedSource == null || _baselineReport == null)
		{
			return;
		}

		_guidedLockCheckScheduled = false;
		var lockedSource = _guidedCapture.LockedSource;
		_guidedReleaseSource = lockedSource;
		_guidedReleaseBaselineReport = _baselineReport.ToArray();
		ApplyGuidedBinding(lockedSource);
		_guidedWaitingForNeutral = true;
		_guidedCapture.Reset();
		UpdateGuidedButtons();
		ShowGuidedPrompt();
	}

	private void ApplyGuidedBinding(ControllerBindingSource source)
	{
		if (_draftProfile == null)
		{
			return;
		}

		var target = ProfileEditor.MappableTargets[_guidedTargetIndex];
		var existingAxis = _draftProfile.Bindings.FirstOrDefault(binding => binding.Target == target)?.Axis;
		var binding = ProfileEditor.CreateBinding(target, source, existingAxis);
		_draftProfile = ProfileEditor.UpsertBinding(_draftProfile, binding);
		SetSelectedTarget(target);
		SetSourceFields(source);
		UpdateBindingList();
		UpdateValidation();
		LoadCalibrationFromTarget();
	}

	private void AdvanceGuidedTarget()
    {
        if (_guidedSingleTarget)
        {
            StopGuidedMapping($"{Friendly(ProfileEditor.MappableTargets[_guidedTargetIndex])} assigned. Review the result in Test.");
            return;
        }
        _guidedTargetIndex++;
		_suggestedSource = null;
		_guidedReleaseSource = null;
		_guidedReleaseBaselineReport = null;
		_useSuggestionButton.IsEnabled = false;
		_ignoreSuggestionButton.IsEnabled = false;
		if (_guidedTargetIndex >= ProfileEditor.MappableTargets.Count)
		{
			StopGuidedMapping($"Mapping complete: {_draftProfile?.Bindings.Count ?? 0} assigned. Unassigned controls are optional. Review the controls below, then open Test.");
			return;
		}

		BeginGuidedArming();
	}

	private void BeginGuidedArming()
	{
		if (_lastReport == null)
		{
			StopGuidedMapping("No controller reports are available.");
			return;
		}

		_baselineReport = _lastReport.ToArray();
		_guidedArming = true;
		_guidedReadyPromptShown = false;
		_guidedWaitingForNeutral = false;
		_guidedLockCheckScheduled = false;
		_guidedReleaseSource = null;
		_guidedReleaseBaselineReport = null;
		_guidedCapture.Reset();
		_guidedArmUntil = DateTimeOffset.UtcNow + GuidedMappingArmDelay;
		UpdateGuidedButtons();
        ShowGuidedPrompt();
        RenderMappingRows();
		_guidedPromptText.BringIntoView();
		SetCaptureStatus("Measuring neutral input before the next action.", ExplicitCaptureStatusHold);
	}

	private void ShowGuidedPrompt()
	{
		if (!_guidedMappingActive)
		{
			return;
		}

		var target = ProfileEditor.MappableTargets[_guidedTargetIndex];
        _guidedProgress.Text = _guidedSingleTarget ? $"Remapping {Friendly(target)}" :
            $"Control {_guidedTargetIndex + 1} of {ProfileEditor.MappableTargets.Count} · {Friendly(target)}";
		_gamepad.Highlight = target;
		SetSelectedTarget(target);
		if (_guidedArming)
		{
			_guidedPromptText.Text = "Release all controls. Measuring neutral input...";
			return;
		}

		if (_guidedWaitingForNeutral)
		{
			_guidedPromptText.Text = $"Assigned {Friendly(target)}. Release the control to finish this step, or choose Continue.";
			SetCaptureStatus($"Locked {target}. Release controls or click Continue.", ExplicitCaptureStatusHold);
			return;
		}

		if (!_guidedReadyPromptShown)
		{
			_guidedPromptText.Text = $"Get ready: {GetGuidedActionText(target)}. Wait for the next prompt.";
			SetCaptureStatus("Ignoring early changes while you get ready.", ExplicitCaptureStatusHold);
			return;
		}

		_guidedPromptText.Text = $"Do this now: {GetGuidedActionText(target)}";
		SetCaptureStatus($"Waiting for {MappingTargetItem.All[_guidedTargetIndex]}...", ExplicitCaptureStatusHold);
	}

	private void UpdateGuidedButtons()
	{
        _guidedProgress.IsVisible = _guidedMappingActive;
		_backGuided.IsVisible = _guidedMappingActive && !_guidedSingleTarget;
        _retryGuided.IsVisible = _stopGuidedMappingButton.IsVisible = _skipGuidedMappingButton.IsVisible = _guidedMappingActive;
        _startGuidedMappingButton.IsVisible = !_guidedMappingActive;
        _backGuided.IsEnabled = _guidedMappingActive && !_guidedSingleTarget && _guidedTargetIndex > 0;
		_retryGuided.IsEnabled = _guidedMappingActive;
		_startGuidedMappingButton.IsEnabled = !_guidedMappingActive;
		_skipGuidedMappingButton.IsEnabled = _guidedMappingActive;
		_skipGuidedMappingButton.Content = _guidedWaitingForNeutral ? "Continue" : "Skip";
		_stopGuidedMappingButton.IsEnabled = _guidedMappingActive;
		UpdateEditorState();
	}

	private bool IsGuidedSourceAlreadyBound(ControllerElement target, ControllerBindingSource source)
	{
		if (_draftProfile == null)
		{
			return false;
		}

		var sourceKey = ProfileEditor.GetSourceKey(source);
		return _draftProfile.Bindings.Any(binding =>
			binding.Target != target &&
			string.Equals(ProfileEditor.GetSourceKey(binding.Source), sourceKey, StringComparison.Ordinal));
	}

	private static string GetGuidedActionText(ControllerElement target)
		=> target switch
		{
			ControllerElement.South => "press the bottom face button (South / A)",
			ControllerElement.East => "press the right face button (East / B)",
			ControllerElement.West => "press the left face button (West / X)",
			ControllerElement.North => "press the top face button (North / Y)",
			ControllerElement.DPadUp => "press D-pad up",
			ControllerElement.DPadDown => "press D-pad down",
			ControllerElement.DPadLeft => "press D-pad left",
			ControllerElement.DPadRight => "press D-pad right",
			ControllerElement.LeftShoulder => "press the left shoulder button",
			ControllerElement.RightShoulder => "press the right shoulder button",
			ControllerElement.Select => "press Select / Back / View",
			ControllerElement.Start => "press Start / Options",
			ControllerElement.Menu => "press Menu / Guide / Home",
			ControllerElement.LeftStickButton => "press the left stick button",
			ControllerElement.RightStickButton => "press the right stick button",
			ControllerElement.LeftStickX => "move the left stick left or right",
			ControllerElement.LeftStickY => "move the left stick up or down",
			ControllerElement.RightStickX => "move the right stick left or right",
			ControllerElement.RightStickY => "move the right stick up or down",
			ControllerElement.LeftTrigger => "pull the left trigger",
			ControllerElement.RightTrigger => "pull the right trigger",
			_ => "press or move " + target
		};

	private void RemoveSelectedBinding()
	{
		if (_draftProfile == null)
		{
			return;
		}

		_draftProfile = ProfileEditor.RemoveBinding(_draftProfile, GetSelectedTarget());
		UpdateBindingList();
		UpdateValidation();
		LoadCalibrationFromTarget();
		SetCaptureStatus($"Removed {GetSelectedTarget()} binding.", ExplicitCaptureStatusHold);
	}

	private void ClearBindings()
	{
		if (_draftProfile == null)
		{
			return;
		}

		StopGuidedMapping("Bindings cleared. Undo restores the previous assignments.");
		_draftProfile = _draftProfile with { Bindings = [] };
		_guidedIgnoredSourceKeys.Clear();
		_guidedCapture.Reset();
		_suggestedSource = null;
		_useSuggestionButton.IsEnabled = false;
		_ignoreSuggestionButton.IsEnabled = false;
		UpdateBindingList();
		UpdateValidation();
		LoadCalibrationFromTarget();
		SetCaptureStatus("Cleared all bindings. Start guided mapping again.", ExplicitCaptureStatusHold);
	}

	private async Task SaveDraftProfileAsync()
    {
        if (_guidedMappingActive || _calibrationActive || _rangeReview) { SetStatus("Finish or cancel the current capture before saving."); return; }
        if (_session != null) await SaveSessionAsync(_session);
    }

    private async Task ImportProfilesAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Import CopperPad profiles",
                FileTypeFilter = [new FilePickerFileType("JSON profiles") { Patterns = ["*.json"] }]
            });
            var file = files.FirstOrDefault();
            if (file == null) return;
            await using var stream = await file.OpenReadAsync();
            var importedProfiles = await JsonControllerProfileSerializer.LoadAsync(stream);
            await ImportDocumentAsync(importedProfiles);
        }
        catch (Exception ex)
        {
            SetStatus("Import failed: " + ex.Message);
            if (!_errors.TryGetValue(("import", null), out var error) || error.Details != ex.Message) ShowImportFailure(ex);
        }
    }

    private async Task ExportProfilesAsync()
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export saved CopperPad profiles",
                SuggestedFileName = "copperpad-profiles.json",
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON profiles") { Patterns = ["*.json"] }]
            });
            if (file == null) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await JsonControllerProfileSerializer.SaveAsync(stream, _profiles);
            ClearUiError("export");
            SetStatus("Exported profiles to " + file.Name);
        }
        catch (Exception ex)
        {
            SetStatus("Export failed: " + ex.Message);
            ShowFailure("export", "Export failed", "The saved document could not be exported. Choose a writable location and retry.", ex, "Retry export", ExportProfilesAsync);
        }
    }

	private void PopulateFieldsFromSelectedTarget()
	{
		if (_draftProfile == null)
		{
			return;
		}

		var binding = _draftProfile.Bindings.FirstOrDefault(binding => binding.Target == GetSelectedTarget());
		if (binding != null)
		{
			SetBindingFields(binding);
		}

		LoadCalibrationFromTarget();
	}

	private void SetBindingFields(ControllerBinding binding)
	{
		SetSelectedTarget(binding.Target);
		SetSourceFields(binding.Source);
	}

	private void SetSourceFields(ControllerBindingSource source)
	{
		_sourceKindBox.SelectedItem = source.Kind;
		_offsetBox.Value = source.Offset;
		_bitBox.Value = source.Bit;
		_hatBox.Value = source.HatValue ?? 0;
		_sourceInvertCheck.IsChecked = source.Invert;
		UpdateSourceFieldAvailability();
	}

	private ControllerBindingSource GetSourceFromFields()
	{
		var kind = _sourceKindBox.SelectedItem is ControllerBindingSourceKind selectedKind
			? selectedKind
			: ControllerBindingSourceKind.ReportBit;
		return new ControllerBindingSource
		{
			Kind = kind,
			Offset = DecimalToInt(_offsetBox.Value),
			Bit = DecimalToInt(_bitBox.Value),
			HatValue = kind == ControllerBindingSourceKind.Hat ? DecimalToInt(_hatBox.Value) : null,
			Invert = _sourceInvertCheck.IsEnabled && _sourceInvertCheck.IsChecked == true
		};
	}

	private ControllerElement GetSelectedTarget()
		=> _targetBox.SelectedItem is MappingTargetItem item ? item.Element : ControllerElement.South;

	private void SetSelectedTarget(ControllerElement target)
	{
		var item = MappingTargetItem.All.FirstOrDefault(item => item.Element == target);
		if (item != null)
		{
			_targetBox.SelectedItem = item;
		}
	}

    private bool StartCalibrationCapture()
    {
        if (!CanCaptureCalibration()) return false;
        var binding = GetCalibrationBinding()!;
        var rest = _rangeReview ? _calibrationCapture?.Center : null;
        _calibrationCapture = new AxisCalibrationCapture();
        if (rest.HasValue) _calibrationCapture.CaptureCenter(rest.Value);
        _calibrationActive = true;
        _calibrationCapture.Observe(ReadCalibrationRaw(binding, _lastReport!));
        lock (_rawReportGate)
        {
            _pendingRangeMinimum = _pendingRangeMaximum = _pendingRangeLast = null;
            _rangeCaptureSource = (_selectedDevice!.Id, binding);
        }
        UpdateCalibrationPreview();
        return true;
    }

    private void StopCalibrationCapture()
    {
        DrainCalibrationReports(endCapture: true);
        _calibrationActive = false;
        UpdateCalibrationPreview();
    }

    private bool CaptureCalibrationCenter()
    {
        if (!CanCaptureCalibration()) return false;
        _calibrationCapture ??= AxisCalibrationCapture.From(GetCalibrationBinding()!.Axis);
        _calibrationCapture.CaptureCenter(ReadCalibrationRaw(GetCalibrationBinding()!, _lastReport!));
        UpdateCalibrationPreview();
        return true;
    }

	private void ApplyCalibration()
	{
		if (_draftProfile == null)
		{
			return;
		}

		var target = CalibrationTarget;
		var binding = GetCalibrationBinding();
		if (binding == null)
		{
			SetCalibrationStatus("No binding selected for calibration.");
			return;
		}

		_calibrationCapture ??= AxisCalibrationCapture.From(binding.Axis);
		var calibration = _calibrationCapture.ToCalibration(_invertCheck.IsChecked == true, _deadzoneSlider.Value, _saturationSlider.Value);
		_draftProfile = ProfileEditor.UpsertBinding(_draftProfile, binding with { Axis = calibration });
		_calibrationActive = false;
		UpdateBindingList();
		UpdateValidation();
		LoadCalibrationFromTarget();
		SetCalibrationStatus($"Applied calibration to {target}.", false);
	}

	private void LoadCalibrationFromTarget()
	{
        // A cancelled capture must not leak pending samples into the next axis/device.
        lock (_rawReportGate)
        {
            _rangeCaptureSource = null;
            _pendingRangeMinimum = _pendingRangeMaximum = _pendingRangeLast = null;
        }
		_loadingCalibration = true;
		_invertCheck.IsVisible = !ProfileEditor.IsTriggerTarget(CalibrationTarget);
		var binding = GetCalibrationBinding();
		var calibration = binding?.Axis;
		_invertCheck.IsChecked = calibration?.Invert ?? false;
		_deadzoneSlider.Value = calibration?.Deadzone ?? 0.1;
		_saturationSlider.Value = calibration?.Saturation ?? 1.0;
		_calibrationCapture = AxisCalibrationCapture.From(calibration);
		_calibrationActive = false;
		_deadzoneNumber.Value = (decimal)_deadzoneSlider.Value;
		_saturationNumber.Value = (decimal)_saturationSlider.Value;
		_loadingCalibration = false;
        _calibrationStage = CalibrationStage.Idle;
        _calibrationStep.Text = "Capture rest, then move through the full range.";
		_calibrationStatusText.Text = "";
        _calibrationStatusText.IsVisible = false;
        UpdateCalibrationControls();
		UpdateCalibrationPreview();
		UpdateCalibrationGraph();
	}

	private ControllerBinding? GetCalibrationBinding()
	{
		if (_draftProfile == null)
		{
			return null;
		}

		var target = CalibrationTarget;
		return _draftProfile.Bindings.FirstOrDefault(binding => binding.Target == target);
	}

	private void UpdateCalibrationPreview()
	{
		if (_calibrationCapture == null)
		{
			_calibrationPreviewText.Text = "Range: none";
			return;
		}

        _calibrationPreviewText.Text = $"Range: {_calibrationCapture.Minimum?.ToString() ?? "—"}..{_calibrationCapture.Maximum?.ToString() ?? "—"} · Rest: {_calibrationCapture.Center?.ToString() ?? "—"}";

	}

	private void UpdateBindingList()
	{
		if (_session != null && _draftProfile != null && !ReferenceEquals(_session.Draft, _draftProfile)) _session.Edit(_draftProfile);
		RenderMappingRows();
		UpdateEditorState();
		SchedulePreview();

	}

    private void UpdateValidation()
    {
        var issues = _session?.Issues ?? Array.Empty<ProfileValidationIssue>();
        _profileNameError.Text = _draftProfile != null && string.IsNullOrWhiteSpace(_draftProfile.Name) ? "A profile name is required." : "";
        _profileNameError.IsVisible = !string.IsNullOrEmpty(_profileNameError.Text);
        _validationText.Text = _session == null ? "Select a controller to edit its mapping." :
            !_session.IsDirty && _draftProfile?.Bindings.Count == 0 ? "Built-in mapping is active. Assign controls to create a custom mapping." :
            issues.Count > 0 ? string.Join("\n", issues.Select(x => x.Message)) : $"{_draftProfile?.Bindings.Count} assigned · Other controls are optional";
        _validationText.Foreground = _session?.IsDirty == true && issues.Count > 0 ? CopperTheme.Error : CopperTheme.Muted;
        UpdateEditorState();
    }

	private void UpdateSelectedDeviceDetails()
	{
        var device = _selectedDevice;
        var mapping = device == null ? null : GetMappingInfo(device);
		if (_session?.IsDirty != true) UpdateOutputLabel();
        _deviceTitle.Text = device?.ProductName ?? "Connect your controller";
        ToolTip.SetTip(_deviceTitle, device?.ProductName);
        UpdateConnectionBadge(_deviceConnected);
        _setupButton.IsVisible = device != null && _tabs.SelectedIndex != 1 && _draftProfile?.Bindings.Count == 0 && (mapping == null || mapping.Source.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase) || mapping.Source.Contains("Fallback", StringComparison.OrdinalIgnoreCase));
        _descriptorText.Text = device == null ? $"No device selected\nProfile document: {_profileStore.Path}" : $"{MappingDisplay.Format(mapping)}\nConnection: {device.Transport}\nProfile document: {_profileStore.Path}\nVID/PID: {device.VendorId:X4}:{device.ProductId:X4}\nReport length: {device.MaxInputReportLength} bytes\n{device.Diagnostic}\n\nDescriptor\n{ToHexRows(device.ReportDescriptor.ToArray())}";
        UpdateEditorState();
	}

	private void ShowSummaryWorkspace()
    {
        _tabs.IsVisible = true;
        _tabs.SelectedIndex = _session?.SelectedPage ?? 0;
        UpdateSelectedDeviceDetails();
    }

	private ControllerProfile? FindSavedProfile(HidDeviceInfo device)
		=> _profiles.FindMatch(new CopperControllerInfo(
			device.Id,
			device.ProductName,
			device.VendorId,
			device.ProductId,
			device.Transport,
			true,
			new HashSet<ControllerProfileKind> { ControllerProfileKind.RawInput },
			ControllerMappingSource.None,
			null,
			device.Diagnostic));

	private ControllerMappingInfo? GetMappingInfo(HidDeviceInfo device)
	{
		try
		{
			var mapping = _host.GetMappingInfo(device.Id);
            ClearUiError("mapping lookup", device.Id);
            return mapping;
		}
		catch (Exception ex)
		{
			SetStatus("Mapping lookup failed: " + ex.Message);
            ShowFailure("mapping lookup", "Mapping lookup failed", $"{device.ProductName}: Refresh the device and try again.", ex, "Refresh devices", () => { RefreshDevices(); return Task.CompletedTask; }, device.Id);
			return null;
		}
	}

	private void PostUi(string context, Action action)
		=> Dispatcher.UIThread.Post(() => TryRunUiAction(context, action));

	private void TryRunUiAction(string context, Action action)
	{
        var deviceId = _selectedDevice?.Id;
		try
		{
			action();
			if (context != "Preview failed") ClearUiError(context, deviceId);
		}
		catch (Exception ex)
		{
			CrashLog.Write(context, ex);
			SetStatus(context + ": " + ex.Message);
            ShowFailure(context, context, ex.Message, ex, context == "Preview failed" ? "Retry preview" : "Open Diagnostics", () => {
                if (context == "Preview failed") { NavigateToError(deviceId, _tabs.SelectedIndex); SchedulePreview(); }
                else NavigateToError(deviceId, 3);
                return Task.CompletedTask;
            }, deviceId);
		}
	}

	private async Task TryRunUiActionAsync(string context, Func<Task> action)
	{
		try
		{
			await action().ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			CrashLog.Write(context, ex);
			SetStatus(context + ": " + ex.Message);
            ShowFailure(context, context, ex.Message, ex, "Open Diagnostics", () => { _tabs.SelectedIndex = 3; return Task.CompletedTask; });
		}
	}

	private void UpdateCaptureStatus(bool force = true)
	{
		var now = DateTimeOffset.UtcNow;
		if (!force)
		{
			if (now < _captureStatusHoldUntil)
			{
				return;
			}

			if (_lastCaptureStatusUpdate != DateTimeOffset.MinValue &&
				now - _lastCaptureStatusUpdate < CaptureStatusUpdateInterval)
			{
				return;
			}
		}

		if (_baselineReport == null)
		{
			SetCaptureStatus("Baseline: none");
			return;
		}

		if (_suggestedSource == null)
		{
			SetCaptureStatus($"Baseline: {_baselineReport.Length} bytes   Change: none");
			return;
		}

		SetCaptureStatus("Suggested source: " + ReportAnalyzer.FormatSource(_suggestedSource));
	}

	private void SetCaptureStatus(string text, TimeSpan? holdFor = null)
	{
		_captureStatusText.Text = text;
		_lastCaptureStatusUpdate = DateTimeOffset.UtcNow;
		_captureStatusHoldUntil = holdFor.HasValue
			? _lastCaptureStatusUpdate + holdFor.Value
			: DateTimeOffset.MinValue;
	}

	private void UpdateSourceFieldAvailability()
	{
		var kind = _sourceKindBox.SelectedItem is ControllerBindingSourceKind selectedKind
			? selectedKind
			: ControllerBindingSourceKind.ReportBit;
		_bitBox.IsEnabled = kind == ControllerBindingSourceKind.ReportBit;
		_hatBox.IsEnabled = kind == ControllerBindingSourceKind.Hat;
		var target = GetSelectedTarget();
		_sourceInvertCheck.IsEnabled = !ProfileEditor.IsAxisTarget(target) ||
			(ProfileEditor.IsTriggerTarget(target) && kind is ControllerBindingSourceKind.ReportBit or ControllerBindingSourceKind.Hat);
	}

	private void SetIndicator(ControllerElement control, bool active)
	{
		if (_indicators.TryGetValue(control, out var border))
		{
			border.Background = active ? Brushes.SeaGreen : Brushes.Transparent;
			border.BorderBrush = active ? Brushes.LightGreen : Brushes.DimGray;
		}
	}

	private Border CreateIndicator(ControllerElement control)
	{
		var border = new Border
		{
			BorderBrush = Brushes.DimGray,
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(4),
			Padding = new Thickness(10, 7),
			Margin = new Thickness(0, 0, 8, 8),
			Child = new TextBlock { Text = control.ToString(), VerticalAlignment = VerticalAlignment.Center }
		};
		_indicators[control] = border;
		return border;
	}

	private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusText.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

	private void SetCalibrationStatus(string text, bool isError = true)
	{
		_calibrationStatusText.Text = text;
        _calibrationStatusText.IsVisible = !string.IsNullOrEmpty(text);
        _calibrationStatusText.Foreground = isError ? CopperTheme.Error : CopperTheme.Success;
        if (isError)
        {
            var deviceId = _selectedDevice?.Id;
            var target = CalibrationTarget;
            ShowUiError("calibration", deviceId, "Calibration needs attention", $"{_selectedDevice?.ProductName} · {Friendly(target)}: {text}", text, "Go to Calibration", () => {
                NavigateToError(deviceId, 2);
                _calibrationTargetBox.SelectedItem = _calibrationTargetBox.Items.OfType<MappingTargetItem>().First(x => x.Element == target);
                RevealErrorControl(_calibrationAction); return Task.CompletedTask;
            });
        }
        else ClearUiError("calibration", _selectedDevice?.Id);
		UpdateCalibrationPreview();
	}

	private static Button Button(string text, EventHandler<RoutedEventArgs> handler)
	{
		var button = new Button { Content = text };
		button.Click += handler;
		return button;
	}

	private static TextBlock TextBlock()
		=> new() { TextWrapping = TextWrapping.Wrap };

	private static TextBlock MonospaceTextBlock()
		=> new()
		{
			TextWrapping = TextWrapping.Wrap,
			FontFamily = new FontFamily("Consolas")
		};

	private static NumericUpDown NumberBox(int min, int max)
		=> new()
		{
			Minimum = min,
			Value = min,
			Maximum = max,
			Increment = 1,
			Width = 130,
			MinWidth = 130
		};

	private static Control LabeledControl(string label, Control control)
	{
		Avalonia.Automation.AutomationProperties.SetName(control, label);
		var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 12, 8) };
		panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
		panel.Children.Add(control);
		return panel;
	}

	private static int DecimalToInt(decimal? value)
		=> (int)(value ?? 0);

	private sealed record PendingRawReport(HidDeviceInfo Device, byte[] Report, DateTimeOffset Timestamp);

	private static bool IsRecoverableHidException(Exception ex)
		=> ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or NotSupportedException;

	private static string ToHexRows(byte[] bytes)
	{
		if (bytes.Length == 0)
		{
			return "";
		}

		var rows = new List<string>();
		for (var offset = 0; offset < bytes.Length; offset += 16)
		{
			var length = Math.Min(16, bytes.Length - offset);
			var hex = string.Join(" ", bytes.Skip(offset).Take(length).Select(value => value.ToString("X2")));
			rows.Add($"{offset:X4}: {hex}");
		}

		return string.Join(Environment.NewLine, rows);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_host.Dispose();
	}

	private sealed class DeviceListItem(HidDeviceInfo device, bool connected)
	{
		public HidDeviceInfo Device { get; } = device;
        public bool Connected { get; } = connected;

		public override string ToString()
			=> $"{Device.ProductName}  0x{Device.VendorId:X4}:0x{Device.ProductId:X4}";
	}


}
