using Avalonia.Controls;
using CopperPad;

namespace CopperPad.Gui;

internal sealed partial class MainWindow
{
    private enum CalibrationStage { Idle, RestCaptured, Capturing, Review }
    private CalibrationStage _calibrationStage;
    private readonly Button _calibrationAction = new() { Content = "Capture rest" };
    private readonly Button _cancelCalibration = new() { Content = "Cancel capture" };
    private readonly Button _retryCalibration = new() { Content = "Retry range" };
    private readonly Button _resetAxis = new() { Content = "Reset this axis" };
    private readonly Button _mapCalibrationAxis = new() { Content = "Map this axis…" };
    private readonly TextBlock _calibrationOutputLabel = TextBlock(), _calibrationLegend = TextBlock();
    private (string DeviceId, ControllerBinding Binding)? _rangeCaptureSource;
    private int? _pendingRangeMinimum, _pendingRangeMaximum, _pendingRangeLast;

    private void DrainCalibrationReports(bool endCapture = false)
    {
        int? minimum, maximum, last;
        lock (_rawReportGate)
        {
            (minimum, maximum, last) = (_pendingRangeMinimum, _pendingRangeMaximum, _pendingRangeLast);
            _pendingRangeMinimum = _pendingRangeMaximum = _pendingRangeLast = null;
            if (endCapture) _rangeCaptureSource = null;
        }
        if (!_calibrationActive || _calibrationCapture == null || minimum == null) return;
        _calibrationCapture.Observe(minimum.Value);
        _calibrationCapture.Observe(maximum!.Value);
        _calibrationCapture.Observe(last!.Value);
        UpdateCalibrationPreview();
    }

    private static double CalibrationOutput(ControllerBinding binding, int raw, AxisCalibration axis) => ProfileEditor.IsTriggerTarget(binding.Target)
        ? InputNormalization.NormalizeTrigger(raw, axis.Minimum, axis.Maximum, axis.Deadzone, axis.Saturation)
        : InputNormalization.NormalizeAxis(raw, axis.Minimum, axis.Maximum, axis.Center, axis.Invert, axis.Deadzone, axis.Saturation);

    private bool TryGetCapturedCalibration(out AxisCalibration? axis, out string? problem)
    {
        axis = null;
        problem = "Move the control through a non-zero range before accepting.";
        if (_calibrationCapture?.HasRange != true) return false;
        var candidate = _calibrationCapture.ToCalibration(_invertCheck.IsChecked == true, _deadzoneSlider.Value, _saturationSlider.Value);
        if (!ProfileEditor.IsTriggerTarget(CalibrationTarget) &&
            (candidate.Center == null || candidate.Center <= candidate.Minimum || candidate.Center >= candidate.Maximum))
        {
            problem = "Move the stick to both sides of its captured center before accepting.";
            return false;
        }
        if (ProfileEditor.IsTriggerTarget(CalibrationTarget) && candidate.Center is { } rest &&
            InputNormalization.NormalizeTrigger(rest, candidate.Minimum, candidate.Maximum, candidate.Deadzone, candidate.Saturation) != 0)
        {
            problem = "The captured trigger would be active at rest. Re-capture rest and range, or map a source that rises when pressed.";
            return false;
        }
        axis = candidate;
        problem = null;
        return true;
    }

    private static bool IsAnalogSource(ControllerBinding binding) => !ProfileEditor.IsTriggerTarget(binding.Target) || binding.Source.Kind is
        ControllerBindingSourceKind.ReportByte or ControllerBindingSourceKind.ReportInt16LittleEndian;

    private static int ReadCalibrationRaw(ControllerBinding binding, byte[] report) => IsAnalogSource(binding)
        ? ProfileControllerMapper.ReadAxisSource(binding.Source, report, report.Length)
        : ReportAnalyzer.ReadSourceValue(binding.Source, report);

    private static bool SourceFitsReport(ControllerBindingSource source, byte[] report) =>
        source.Offset >= 0 && source.Offset < report.Length &&
        (source.Kind == ControllerBindingSourceKind.ReportInt16LittleEndian ? 2 : 1) <= report.Length - source.Offset;

    private bool CanCaptureCalibration()
    {
        var binding = GetCalibrationBinding();
        if (binding == null) { SetCalibrationStatus("Assign a raw source in Mapping before calibration. Built-in sources cannot be edited here."); return false; }
        if (!IsAnalogSource(binding)) { SetCalibrationStatus("This is a digital control. Its output is 0 or 1 and needs no range calibration."); return false; }
        if (!_deviceConnected || _lastReport == null || !SourceFitsReport(binding.Source, _lastReport))
        { SetCalibrationStatus("Waiting for a live report. Reconnect or move the controller, then capture rest."); return false; }
        return true;
    }

    private void AdvanceCalibration()
    {
        switch (_calibrationStage)
        {
            case CalibrationStage.Idle:
                if (!CanCaptureCalibration()) return;
                _calibrationCapture = new AxisCalibrationCapture();
                if (!CaptureCalibrationCenter()) return;
                _rangeReview = true;
                _calibrationStage = CalibrationStage.RestCaptured;
                _calibrationStep.Text = "Rest captured. Start range capture, then move to every limit.";
                break;
            case CalibrationStage.RestCaptured:
                if (!StartCalibrationCapture()) return;
                _calibrationStage = CalibrationStage.Capturing;
                _calibrationStep.Text = "Recording… Move through the full range, then review.";
                break;
            case CalibrationStage.Capturing:
                StopCalibrationCapture();
                _calibrationStage = CalibrationStage.Review;
                _calibrationStep.Text = "Review the captured range. Accept it or retry.";
                break;
            case CalibrationStage.Review:
                AcceptCalibrationRange();
                return;
        }
        _calibrationStatusText.Text = "";
        _calibrationStatusText.IsVisible = false;
        ClearUiError("calibration", _selectedDevice?.Id);
        UpdateCalibrationControls();
        UpdateEditorState();
        UpdateCalibrationGraph();
    }

    private void RetryCalibrationRange()
    {
        if (_calibrationStage != CalibrationStage.Review || !StartCalibrationCapture()) return;
        _calibrationStage = CalibrationStage.Capturing;
        _calibrationStep.Text = "Recording a fresh range… Move to every limit.";
        _calibrationStatusText.Text = "";
        _calibrationStatusText.IsVisible = false;
        ClearUiError("calibration", _selectedDevice?.Id);
        UpdateCalibrationControls();
        UpdateEditorState();
    }

    private void CancelCalibration()
    {
        _rangeReview = false;
        ClearUiError("calibration", _selectedDevice?.Id);
        LoadCalibrationFromTarget();
        _calibrationStep.Text = "Capture cancelled. Your existing calibration is unchanged.";
        SchedulePreview();
        UpdateEditorState();
    }

    private void UpdateCalibrationControls()
    {
        var binding = GetCalibrationBinding();
        var analog = binding != null && IsAnalogSource(binding);
        _calibrationAction.Content = _calibrationStage switch
        {
            CalibrationStage.RestCaptured => "Start range capture",
            CalibrationStage.Capturing => "Review range",
            CalibrationStage.Review => "Accept range",
            _ => "Capture rest"
        };
        // Leave the action available before input arrives so it can explain why capture is unavailable.
        _calibrationAction.IsEnabled = analog && !_guidedMappingActive;
        _cancelCalibration.IsVisible = _rangeReview || _calibrationActive;
        _retryCalibration.IsVisible = _calibrationStage == CalibrationStage.Review;
        _mapCalibrationAxis.IsVisible = binding == null && _selectedDevice != null;
        _calibrationPreviewText.IsVisible = analog;
        _resetAxis.IsEnabled = analog && !_rangeReview && !_calibrationActive;
        _deadzoneSlider.IsEnabled = _saturationSlider.IsEnabled = _deadzoneNumber.IsEnabled = _saturationNumber.IsEnabled = _invertCheck.IsEnabled = analog;
        if (!_rangeReview && !_calibrationActive)
            _calibrationStep.Text = _session == null ? "Connect a controller to calibrate its movement." : binding == null ? "Assign a raw source to calibrate this control." : !analog ? "Digital input · Output is 0 or 1. No calibration needed." : _calibrationStep.Text;
    }
}
