using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CopperPad.Gui;

internal sealed partial class MainWindow
{
    private sealed record UiError(string Title, string Message, string Details, string ActionLabel, Func<Task> Action, long Sequence = 0);
    private readonly Dictionary<(string Operation, string? DeviceId), UiError> _errors = new();
    private readonly Border _errorBanner = new() { IsVisible = false };
    private readonly TextBlock _errorTitle = TextBlock(), _errorMessage = TextBlock();
    private readonly Button _errorAction = new(), _errorDetails = new() { Content = "Details…" };
    private UiError? _displayedError;
    private long _errorSequence;

    private Control BuildErrorBanner()
    {
        _errorBanner.Background = CopperTheme.ErrorSurface;
        _errorBanner.BorderBrush = CopperTheme.Error;
        _errorBanner.BorderThickness = new Thickness(2, 0, 0, 0);
        _errorBanner.CornerRadius = new CornerRadius(6);
        _errorBanner.Padding = new Thickness(12, 8);
        _errorBanner.Margin = new Thickness(0, 0, 0, 8);
        AutomationProperties.SetName(_errorBanner, "Errors requiring attention");
        _errorTitle.FontWeight = FontWeight.SemiBold;
        _errorTitle.Foreground = CopperTheme.Error;
        AutomationProperties.SetLiveSetting(_errorTitle, AutomationLiveSetting.Assertive);
        _errorTitle.MaxLines = 1;
        _errorTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _errorMessage.MaxLines = 2;
        _errorMessage.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { _errorTitle, _errorMessage } });
        var actions = Actions(_errorAction, _errorDetails);
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 1); row.Children.Add(actions);
        _errorBanner.Child = row;
        _errorAction.Click += async (_, _) => {
            if (_displayedError is { } error) await TryRunUiActionAsync(error.Title, error.Action);
        };
        _errorDetails.Click += async (_, _) => await ShowErrorDetailsAsync();
        return _errorBanner;
    }

    private void ShowUiError(string operation, string? deviceId, string title, string message, string details, string actionLabel, Func<Task> action)
    {
        // Repeated reports must not replace an unrelated failure or bury it in the status footer.
        var key = (operation, deviceId);
        var sequence = _errors.TryGetValue(key, out var previous) && previous.Details == details && previous.Message == message ? previous.Sequence : ++_errorSequence;
        _errors[key] = new UiError(title, message, details, actionLabel, action, sequence);
        UpdateErrorBanner();
    }

    private void ClearUiError(string operation, string? deviceId = null)
    {
        _errors.Remove((operation, deviceId));
        UpdateErrorBanner();
    }

    private void ShowFailure(string operation, string title, string message, Exception exception, string actionLabel, Func<Task> action, string? deviceId = null) =>
        ShowUiError(operation, deviceId, title, message, exception.Message, actionLabel, action);

    private void ShowDeviceFailure(Exception exception) => ShowFailure("devices", "Controller scan failed", "Reconnect the controller and refresh devices.", exception,
        "Refresh devices", () => { RefreshDevices(); return Task.CompletedTask; });

    private void ShowInputFailure(string deviceId, string message) => ShowUiError("input", deviceId, "Controller input failed", message, message,
        "Retry connection", () => { NavigateToError(deviceId, 0); RefreshDevices(); return Task.CompletedTask; });

    private void ShowImportFailure(Exception exception) => ShowFailure("import", "Import failed", "The saved document was not replaced. Your remaining drafts are retained. Fix the document or choose another file.", exception,
        "Retry import", ImportProfilesAsync);

    private void ClearEditingErrors(string? deviceId)
    {
        foreach (var operation in new[] { "assignment", "capture", "calibration", "save", "Preview failed" }) _errors.Remove((operation, deviceId));
        if (_selectedDevice?.Id == deviceId) _assignmentError.IsVisible = false;
        UpdateErrorBanner();
    }

    private void ShowCaptureError(string message, bool advanced = false)
    {
        var deviceId = _selectedDevice?.Id;
        _guidedPromptText.Text = message;
        _guidedPromptText.Foreground = CopperTheme.Error;
        ShowUiError("capture", deviceId, "Input capture unavailable", message, message, advanced ? "Go to Advanced" : "Go to Mapping", () => {
            NavigateToError(deviceId, 1);
            if (advanced) { _advancedEditor.IsExpanded = true; RevealErrorControl(_offsetBox); }
            else RevealErrorControl(_startGuidedMappingButton);
            return Task.CompletedTask;
        });
    }

    private UiError[] CurrentErrors()
    {
        // A reverted or saved draft no longer needs a save retry.
        foreach (var key in _errors.Keys.Where(k => k.Operation == "save" && (!_sessions.TryGetValue(k.DeviceId ?? "", out var s) || !s.IsDirty)).ToArray())
            _errors.Remove(key);
        var errors = _errors.Values.OrderByDescending(e => e.Sequence).ToList();
        foreach (var session in _sessions.Values.Where(s => s.IsDirty && s.Issues.Count > 0).OrderBy(s => s != _session))
        {
            var messages = string.Join("\n", session.Issues.Select(i => i.Message));
            errors.Add(new UiError("Changes cannot be saved", $"{session.Device.ProductName}: {session.Issues[0].Message}", messages, "Fix mapping", () => {
                NavigateToError(session.Device.Id, 1);
                if (string.IsNullOrWhiteSpace(session.Draft.Name)) RevealErrorControl(_profileName);
                else if (session.Draft.Bindings.Count == 0) RevealErrorControl(_startGuidedMappingButton);
                else
                {
                    var invalid = session.Draft.Bindings.FirstOrDefault(b => ProfileEditor.ValidateProfile(session.Draft with { Name = "Validation", Bindings = new[] { b } }, session.Device.MaxInputReportLength).Count > 0);
                    if (invalid != null) SetBindingFields(invalid);
                    _advancedEditor.IsExpanded = true; RevealErrorControl(_offsetBox);
                }
                return Task.CompletedTask;
            }));
        }
        return errors.ToArray();
    }

    private void UpdateErrorBanner()
    {
        var errors = CurrentErrors();
        _displayedError = errors.FirstOrDefault();
        _errorBanner.IsVisible = _displayedError != null;
        if (_displayedError == null) return;
        _errorTitle.Text = "Error · " + _displayedError.Title + (errors.Length > 1 ? $" · {errors.Length} issues" : "");
        _errorMessage.Text = _displayedError.Message;
        _errorAction.Content = _displayedError.ActionLabel;
        AutomationProperties.SetName(_errorAction, _displayedError.ActionLabel + ": " + _displayedError.Title);
    }

    private void NavigateToError(string? deviceId, int page)
    {
        if (deviceId != null && _selectedDevice?.Id != deviceId && _sessions.TryGetValue(deviceId, out var session))
        {
            var item = _deviceList.Items.OfType<DeviceListItem>().FirstOrDefault(i => i.Device.Id == deviceId);
            if (item != null) _deviceList.SelectedItem = item;
            else SelectDevice(session.Device);
        }
        _tabs.SelectedIndex = page;
    }

    private static void RevealErrorControl(Control control) => Dispatcher.UIThread.Post(() => {
        control.BringIntoView();
        control.Focus();
    }, DispatcherPriority.Loaded);

    private async Task ShowErrorDetailsAsync()
    {
        var details = string.Join("\n\n", CurrentErrors().Select(e => e.Title + "\n" + e.Message + "\n" + e.Details));
        if (DialogHandler != null) { await DialogHandler("Error details", details, ["Close"]); return; }
        var dialog = new Window { Title = "Error details", Width = 620, Height = 420, MinWidth = 420, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new Thickness(20), RowSpacing = 12 };
        root.Children.Add(Heading("Error details", 21));
        var text = new SelectableTextBlock { Text = details, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, monospace") };
        AutomationProperties.SetName(text, "Full error details");
        var scroll = Scroll(text); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var copy = Button("Copy details", async (_, _) => { if (dialog.Clipboard != null) await dialog.Clipboard.SetTextAsync(details); });
        var actions = Actions(copy, Button("Close", (_, _) => dialog.Close()));
        Grid.SetRow(actions, 2); root.Children.Add(actions); dialog.Content = root;
        await dialog.ShowDialog(this);
    }
}
