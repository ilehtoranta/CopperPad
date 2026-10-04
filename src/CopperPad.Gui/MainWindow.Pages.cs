using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CopperPad;

namespace CopperPad.Gui;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, bool> _mappingGroupStates = new();

    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(16, 12) };
        root.Children.Add(BuildErrorBanner());
        var body = new Grid { ColumnDefinitions = new("250,12,*") };
        body.ColumnDefinitions[0].MinWidth = 180;
        body.ColumnDefinitions[0].MaxWidth = 330;
        Grid.SetRow(body, 1); root.Children.Add(body);
        body.Children.Add(BuildDevicePane());
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Avalonia.Automation.AutomationProperties.SetName(splitter, "Resize controller sidebar");
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var workspace = BuildWorkspace(); Grid.SetColumn(workspace, 2); body.Children.Add(workspace);
        var save = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 8, 0, 4) };
        save.Children.Add(_saveState); _saveState.VerticalAlignment = VerticalAlignment.Center;
        _saveProfileButton.Content = "Save changes"; _saveProfileButton.Classes.Add("primary");
        _saveProfileButton.Click += async (_, _) => await SaveDraftProfileAsync();
        _saveAllButton.Click += async (_, _) => await SaveAllDraftsAsync();
        _revertButton.Click += (_, _) => {
            if (_session == null) return;
            StopGuidedMapping("Changes reverted."); _session.Revert(); ClearEditingErrors(_session.Device.Id); RestoreSession(); ApplyDeviceFilter();
        };
        var actions = Actions(_saveAllButton, _revertButton, _saveProfileButton);
        Grid.SetColumn(actions, 1); save.Children.Add(actions);
        Grid.SetRow(save, 2); root.Children.Add(save);
        _statusText.Foreground = CopperTheme.Muted;
        _statusText.IsVisible = false;
        Grid.SetRow(_statusText, 3); root.Children.Add(_statusText);
        return root;
    }

    private Control BuildDevicePane()
    {
        var pane = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), MinWidth = 180 };
        var brand = Heading("CopperPad", 20); brand.Foreground = CopperTheme.Copper; pane.Children.Add(brand);
        _deviceFilterText.Margin = new Thickness(0, 4, 0, 8); _deviceFilterText.Foreground = CopperTheme.Muted;
        Grid.SetRow(_deviceFilterText, 1); pane.Children.Add(_deviceFilterText);
        _deviceList.SelectionChanged += (_, _) => { if (!_updatingDeviceList && _deviceList.SelectedItem is DeviceListItem item) SelectDevice(item.Device); };
        ScrollViewer.SetHorizontalScrollBarVisibility(_deviceList, ScrollBarVisibility.Disabled);
        _deviceList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DeviceListItem>((item, _) => {
            var entry = new StackPanel { Spacing = 4, Margin = new Thickness(2, 6), Children = { new TextBlock { Text = item?.Device.ProductName, TextWrapping = TextWrapping.Wrap } } };
            if (item?.Connected != true) entry.Children.Add(new TextBlock { Text = "Disconnected · Draft available", Foreground = CopperTheme.Warning, TextWrapping = TextWrapping.Wrap });
            return entry;
        });
        Grid.SetRow(_deviceList, 2); pane.Children.Add(_deviceList);
        _showAllDevicesCheck.Content = Text("Show all HID devices");
        _showAllDevicesCheck.IsCheckedChanged += (_, _) => ApplyDeviceFilter();
        var export = Button("Export saved profiles…", async (_, _) => await ExportProfilesAsync());
        export.Content = Text("Export saved profiles…"); export.HorizontalContentAlignment = HorizontalAlignment.Left;
        var options = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0), Children = {
            Button("Refresh devices", (_, _) => RefreshDevices()),
            new Expander { Header = "Options", HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 6, Children = {
                _showAllDevicesCheck, Button("Import profiles…", async (_, _) => await ImportProfilesAsync()), export
            } } }
        } };
        Grid.SetRow(options, 3); pane.Children.Add(options);
        return pane;
    }

    private Control BuildWorkspace()
    {
        var root = new Grid { RowDefinitions = new("Auto,*"), MinWidth = 360 };
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 0, 6) };
        _deviceTitle.FontSize = 20; _deviceTitle.FontWeight = FontWeight.SemiBold;
        _deviceTitle.MaxLines = 2; _deviceTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        title.Children.Add(_deviceTitle);
        _deviceSubtitle.FontSize = 12;
        _connectionBadge.Background = CopperTheme.Surface;
        _connectionBadge.CornerRadius = new CornerRadius(4);
        _connectionBadge.Padding = new Thickness(8, 3);
        _connectionBadge.VerticalAlignment = VerticalAlignment.Top;
        _connectionBadge.Child = _deviceSubtitle;
        Grid.SetColumn(_connectionBadge, 1); title.Children.Add(_connectionBadge); header.Children.Add(title);
        _setupButton.Click += (_, _) => { _tabs.SelectedIndex = 1; _guidedPromptText.Text = "Start guided setup, or remap individual controls below."; };
        _setupButton.MinHeight = 30; _setupButton.FontSize = 13; _setupButton.Padding = new Thickness(10, 5);
        _previewText.FontSize = 13; _previewText.VerticalAlignment = VerticalAlignment.Center;
        var details = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        details.Children.Add(_previewText); Grid.SetColumn(_setupButton, 1); details.Children.Add(_setupButton); header.Children.Add(details);
        root.Children.Add(header);
        _tabs.ItemsSource = new[] {
            new TabItem { Header = "Test", Content = BuildTestTab() },
            new TabItem { Header = "Mapping", Content = BuildMapTab() },
            new TabItem { Header = "Calibration", Content = BuildCalibrationTab() },
            new TabItem { Header = "Diagnostics", Content = BuildReportsTab() }
        };
        _tabs.SelectionChanged += (_, e) => {
            if (e.Source != _tabs) return;
            if (_session != null) _session.SelectedPage = _tabs.SelectedIndex;
            if (_tabs.SelectedIndex != 1 && _guidedMappingActive) StopGuidedMapping("Completed assignments retained. Review them in Test.");
            if (_tabs.SelectedIndex != 2 && (_rangeReview || _calibrationActive)) CancelCalibration();
            UpdateSelectedDeviceDetails();
        };
        Grid.SetRow(_tabs, 1); root.Children.Add(_tabs);
        return root;
    }

    private Control BuildTestTab()
    {
        var panel = Page("Test", "Press buttons and move the sticks to check the active mapping.");
        var toggle = new CheckBox { Content = "Use labeled control grid" };
        var grid = BuildControlGrid(); grid.IsVisible = false;
        _gamepadCard = Card(_gamepad);
        toggle.IsCheckedChanged += (_, _) => { grid.IsVisible = toggle.IsChecked == true; _gamepadCard.IsVisible = toggle.IsChecked != true; };
        panel.Children.Add(toggle); panel.Children.Add(_liveNumbers); panel.Children.Add(_gamepadCard); panel.Children.Add(grid);
        return Scroll(panel);
    }

    private Control BuildMapTab()
    {
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(12) };
        var header = new StackPanel { Spacing = 5 };
        _profileName.PlaceholderText = "Custom profile name";
        Avalonia.Automation.AutomationProperties.SetName(_profileName, "Custom profile name");
        _profileName.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty || _loadingEditor || _draftProfile == null) return; _draftProfile = _draftProfile with { Name = _profileName.Text ?? "" }; CommitEditorChange(); };
        var name = new Grid { ColumnDefinitions = new("Auto,*") };
        name.Children.Add(new TextBlock { Text = "Custom profile", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(_profileName, 1); name.Children.Add(_profileName); header.Children.Add(name);
        _profileNameError.Foreground = CopperTheme.Error; _profileNameError.IsVisible = false; header.Children.Add(_profileNameError);
        _guidedPromptText.Text = "Use guided setup, or remap one control below."; _guidedPromptText.FontSize = 15;
        _guidedProgress.Foreground = CopperTheme.Copper; _guidedProgress.IsVisible = false;
        _startGuidedMappingButton.Content = "Guided setup"; _startGuidedMappingButton.Classes.Add("primary");
        _startGuidedMappingButton.Click += (_, _) => StartGuidedMapping();
        _skipGuidedMappingButton.Click += (_, _) => SkipGuidedTarget();
        _stopGuidedMappingButton.Click += (_, _) => StopGuidedMapping("Setup stopped. Completed assignments are retained.");
        _backGuided.Click += (_, _) => RestartGuidedTarget(Math.Max(0, _guidedTargetIndex - 1));
        _retryGuided.Click += (_, _) => RestartGuidedTarget(_guidedTargetIndex);
        _backGuided.IsVisible = _retryGuided.IsVisible = _skipGuidedMappingButton.IsVisible = _stopGuidedMappingButton.IsVisible = false;
        var guided = new StackPanel { Spacing = 3, Children = { _guidedProgress, _guidedPromptText,
            Actions(_startGuidedMappingButton, _backGuided, _retryGuided, _skipGuidedMappingButton, _stopGuidedMappingButton, Button("Review in Test →", (_, _) => { StopGuidedMapping("Review your assignments in Test."); _tabs.SelectedIndex = 0; })) } };
        header.Children.Add(guided);
        _undoButton.Click += (_, _) => UndoEdit(false); _redoButton.Click += (_, _) => UndoEdit(true);
        _clearAllButton.Click += (_, _) => ClearBindings();
        _openAdvancedButton.Click += (_, _) => { _advancedEditor.IsExpanded = true; _advancedEditor.BringIntoView(); };
        header.Children.Add(Actions(_undoButton, _redoButton, _clearAllButton, _openAdvancedButton));
        header.Children.Add(_validationText); root.Children.Add(header);
        var body = new StackPanel { Spacing = 8, Children = { _mappingRows } };
        _targetBox.ItemsSource = MappingTargetItem.All; _targetBox.SelectedIndex = 0;
        _targetBox.SelectionChanged += (_, _) => PopulateFieldsFromSelectedTarget();
        _sourceKindBox.ItemsSource = Enum.GetValues<ControllerBindingSourceKind>(); _sourceKindBox.SelectedItem = ControllerBindingSourceKind.ReportBit;
        _sourceKindBox.SelectionChanged += (_, _) => UpdateSourceFieldAvailability();
        _useSuggestionButton.Content = "Assign detected input";
        _useSuggestionButton.Click += (_, _) => { if (_suggestedSource != null) { SetSourceFields(_suggestedSource); AddOrUpdateBinding(); } };
        _ignoreSuggestionButton.Click += (_, _) => IgnoreSuggestedSource();
        var fields = new WrapPanel();
        foreach (var field in new[] { LabeledControl("Control", _targetBox), LabeledControl("Source kind", _sourceKindBox), LabeledControl("Byte offset", _offsetBox), LabeledControl("Bit", _bitBox), LabeledControl("Hat value", _hatBox) }) fields.Children.Add(field);
        _assignmentError.Foreground = CopperTheme.Error; _assignmentError.IsVisible = false;
        _advancedEditor.Content = new StackPanel { Spacing = 8, Children = { fields, _sourceInvertCheck, _assignmentError,
            Actions(Button("Capture baseline", (_, _) => CaptureBaseline()), _useSuggestionButton, _ignoreSuggestionButton, Button("Assign input", (_, _) => AddOrUpdateBinding())), _captureStatusText } };
        body.Children.Add(_advancedEditor);
        var scroll = Scroll(body); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        UpdateSourceFieldAvailability();
        return root;
    }

    private Control BuildCalibrationTab()
    {
        var panel = Page("Calibration", "Tune mapped analog controls. Changes update your draft immediately.");
        _calibrationTargetBox.ItemsSource = AxisTargets.Select(x => new MappingTargetItem(x)).ToArray();
        _calibrationTargetBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MappingTargetItem>((item, _) => Text(item == null ? "" : Friendly(item.Element)));
        _calibrationTargetBox.SelectedIndex = 0;
        _calibrationTargetBox.SelectionChanged += (_, _) => { _rangeReview = false; LoadCalibrationFromTarget(); UpdateEditorState(); SchedulePreview(); };
        var selector = new Grid { ColumnDefinitions = new("Auto,*") };
        selector.Children.Add(new TextBlock { Text = "Axis / trigger", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        Avalonia.Automation.AutomationProperties.SetName(_calibrationTargetBox, "Axis / trigger");
        Grid.SetColumn(_calibrationTargetBox, 1); selector.Children.Add(_calibrationTargetBox); panel.Children.Add(selector);
        _calibrationStatusText.Foreground = CopperTheme.Error; _calibrationStatusText.IsVisible = false; panel.Children.Add(_calibrationStatusText);
        _calibrationAction.Classes.Add("primary"); _calibrationAction.Click += (_, _) => AdvanceCalibration();
        _cancelCalibration.Click += (_, _) => CancelCalibration(); _retryCalibration.Click += (_, _) => RetryCalibrationRange();
        _mapCalibrationAxis.Click += (_, _) => {
            _tabs.SelectedIndex = 1; SetSelectedTarget(CalibrationTarget); _advancedEditor.IsExpanded = true; _advancedEditor.BringIntoView();
            _guidedPromptText.Text = $"Assign {Friendly(CalibrationTarget)} using Remap, or Assign input in Advanced.";
        };
        _resetAxis.Click += (_, _) => ResetCalibration();
        var workflow = new StackPanel { Spacing = 5, Children = { Heading("Range capture", 16), _calibrationStep,
            Actions(_calibrationAction, _mapCalibrationAxis, _retryCalibration, _cancelCalibration), _calibrationPreviewText } };
        _calibrationValues.FontSize = 12;
        _calibrationLegend.FontSize = 12; _calibrationOutputLabel.FontSize = 12;
        var readout = Card(new StackPanel { Spacing = 4, Children = { _calibrationOutputLabel, _calibrationGraph, _calibrationLegend, _calibrationValues } });
        var range = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        range.Children.Add(workflow); Grid.SetColumn(readout, 1); range.Children.Add(readout);
        range.SizeChanged += (_, _) => {
            var narrow = range.Bounds.Width < 520;
            range.ColumnDefinitions = new(narrow ? "*" : "*,*"); range.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto");
            Grid.SetColumn(readout, narrow ? 0 : 1); Grid.SetRow(readout, narrow ? 1 : 0);
        };
        panel.Children.Add(range);
        _deadzoneSlider.Width = _saturationSlider.Width = 140;
        _deadzoneNumber.Width = _saturationNumber.Width = 80;
        _deadzoneSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) CalibrationSettingChanged(); };
        _saturationSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) CalibrationSettingChanged(); };
        _invertCheck.IsCheckedChanged += (_, _) => CalibrationSettingChanged();
        _deadzoneNumber.ValueChanged += (_, _) => { if (!_loadingCalibration) _deadzoneSlider.Value = (double)(_deadzoneNumber.Value ?? 0); };
        _saturationNumber.ValueChanged += (_, _) => { if (!_loadingCalibration) _saturationSlider.Value = (double)(_saturationNumber.Value ?? 1); };
        Avalonia.Automation.AutomationProperties.SetName(_deadzoneSlider, "Deadzone");
        Avalonia.Automation.AutomationProperties.SetName(_deadzoneNumber, "Deadzone value");
        Avalonia.Automation.AutomationProperties.SetName(_saturationSlider, "Saturation");
        Avalonia.Automation.AutomationProperties.SetName(_saturationNumber, "Saturation value");
        var settings = new WrapPanel();
        settings.Children.Add(LabeledControl("Deadzone", new StackPanel { Children = { Actions(_deadzoneSlider, _deadzoneNumber), Text("Ignore small movements near rest.") } }));
        settings.Children.Add(LabeledControl("Saturation", new StackPanel { Children = { Actions(_saturationSlider, _saturationNumber), Text("Reach full output before the limit.") } }));
        panel.Children.Add(settings); panel.Children.Add(Actions(_invertCheck, _resetAxis));
        return Scroll(panel);
    }

    private Control BuildReportsTab()
    {
        var panel = Page("Diagnostics", "Inspect the selected device’s HID reports and descriptor.");
        var pause = Button("Pause reports", (_, _) => { });
        pause.Click += (_, _) => { _diagnosticsPaused = !_diagnosticsPaused; pause.Content = _diagnosticsPaused ? "Resume reports" : "Pause reports"; };
        panel.Children.Add(Actions(pause, Button("Copy report", async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(_rawHexText.Text ?? ""); }), Button("Copy details", async (_, _) => { if (Clipboard != null) await Clipboard.SetTextAsync(_descriptorText.Text ?? ""); })));
        panel.Children.Add(_reportRateText); panel.Children.Add(_changedBytesText);
        panel.Children.Add(Heading("Raw input report", 17)); panel.Children.Add(Card(_rawHexText));
        panel.Children.Add(Heading("Device details & descriptor", 17)); panel.Children.Add(Card(_descriptorText));
        return Scroll(panel);
    }

    private void RenderMappingRows()
    {
        _mappingRows.Children.Clear();
        foreach (var group in ProfileEditor.MappableTargets.GroupBy(x => ProfileEditor.IsAxisTarget(x) ? "Sticks & triggers" : ProfileEditor.IsDPadTarget(x) ? "Directional pad" : "Buttons"))
        {
            var rows = new StackPanel { Spacing = 3 };
            var assigned = group.Count(target => _draftProfile?.Bindings.Any(x => x.Target == target) == true);
            var expander = new Expander { Header = $"{group.Key} · {assigned}/{group.Count()} assigned", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = _mappingGroupStates.GetValueOrDefault(group.Key, group.Key == "Buttons") || _guidedMappingActive && group.Contains(ProfileEditor.MappableTargets[_guidedTargetIndex]), Content = rows };
            expander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) _mappingGroupStates[group.Key] = expander.IsExpanded; };
            foreach (var target in group)
            {
                var binding = _draftProfile?.Bindings.FirstOrDefault(x => x.Target == target);
                var row = new Grid { ColumnDefinitions = new("*,Auto") };
                var label = new StackPanel { Spacing = 1, Children = { Text(Friendly(target)), new TextBlock { Text = binding == null ? "Unassigned · optional" : ReportAnalyzer.FormatSource(binding.Source), FontSize = 12, Foreground = binding == null ? CopperTheme.Muted : CopperTheme.Success, TextWrapping = TextWrapping.Wrap } } };
                if (binding != null && _draftProfile != null && _selectedDevice != null)
                {
                    var issues = ProfileEditor.ValidateProfile(_draftProfile with { Name = "Row validation", Bindings = new[] { binding } }, _selectedDevice.MaxInputReportLength);
                    foreach (var issue in issues) label.Children.Add(new TextBlock { Text = issue.Message, Foreground = CopperTheme.Error, TextWrapping = TextWrapping.Wrap });
                }
                row.Children.Add(label);
                var remap = Button("Remap", (_, _) => RemapControl(target)); remap.IsEnabled = _selectedDevice != null && !_guidedMappingActive;
                Avalonia.Automation.AutomationProperties.SetName(remap, $"Remap {Friendly(target)}");
                var clear = Button("Clear", (_, _) => { SetSelectedTarget(target); RemoveSelectedBinding(); }); clear.IsEnabled = binding != null && !_guidedMappingActive;
                Avalonia.Automation.AutomationProperties.SetName(clear, $"Clear {Friendly(target)}");
                remap.MinHeight = clear.MinHeight = 30; remap.Padding = clear.Padding = new Thickness(10, 5);
                var actions = Actions(remap, clear); Grid.SetColumn(actions, 1); row.Children.Add(actions);
                var card = Card(row); card.Padding = new Thickness(8, 5);
                if (_guidedMappingActive && target == ProfileEditor.MappableTargets[_guidedTargetIndex]) { card.BorderBrush = CopperTheme.Copper; card.BorderThickness = new Thickness(2); }
                rows.Children.Add(card);
                if (_guidedMappingActive && target == ProfileEditor.MappableTargets[_guidedTargetIndex])
                    Dispatcher.UIThread.Post(() => { if (!_disposed && _guidedMappingActive && ProfileEditor.MappableTargets[_guidedTargetIndex] == target) card.BringIntoView(); }, DispatcherPriority.Loaded);
            }
            _mappingRows.Children.Add(expander);
        }
    }

    private static string Friendly(ControllerElement target) => target switch {
        ControllerElement.LeftStickX => "Left stick · Horizontal", ControllerElement.LeftStickY => "Left stick · Vertical",
        ControllerElement.RightStickX => "Right stick · Horizontal", ControllerElement.RightStickY => "Right stick · Vertical",
        ControllerElement.South => "A / South", ControllerElement.East => "B / East", ControllerElement.West => "X / West", ControllerElement.North => "Y / North",
        ControllerElement.LeftShoulder => "Left shoulder", ControllerElement.RightShoulder => "Right shoulder",
        ControllerElement.LeftStickButton => "Left stick press", ControllerElement.RightStickButton => "Right stick press",
        ControllerElement.LeftTrigger => "Left trigger", ControllerElement.RightTrigger => "Right trigger",
        ControllerElement.DPadUp => "D-pad up", ControllerElement.DPadDown => "D-pad down", ControllerElement.DPadLeft => "D-pad left", ControllerElement.DPadRight => "D-pad right",
        _ => new MappingTargetItem(target).ToString()
    };
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Heading(string value, double size) => new() { Text = value, FontSize = size, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private static StackPanel Page(string title, string help) => new() { Spacing = 8, Margin = new Thickness(12), Children = { new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = CopperTheme.Muted } } };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static Border Card(Control content) => new() { Background = CopperTheme.Surface, BorderBrush = CopperTheme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = content };
    private static WrapPanel Actions(params Control[] controls)
    {
        var result = new WrapPanel();
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 6, 3); result.Children.Add(control); }
        return result;
    }
	private Control BuildControlGrid()
	{
		var root = new Grid
		{
			ColumnDefinitions = new ColumnDefinitions("*,*"),
			RowDefinitions = new RowDefinitions("Auto,Auto,*"),
			Margin = new Thickness(8)
		};
		var triggerPanel = new Grid
		{
			ColumnDefinitions = new ColumnDefinitions("*,*"),
			Margin = new Thickness(0, 0, 0, 16)
		};
		triggerPanel.Children.Add(LabeledControl("Left trigger", _leftTrigger));
		var rightTrigger = LabeledControl("Right trigger", _rightTrigger);
		Grid.SetColumn(rightTrigger, 1);
		triggerPanel.Children.Add(rightTrigger);
		Grid.SetColumnSpan(triggerPanel, 2);
		root.Children.Add(triggerPanel);

		var leftStickPanel = LabeledControl("Left stick", _leftStick);
		Grid.SetRow(leftStickPanel, 1);
		root.Children.Add(leftStickPanel);
		var rightStickPanel = LabeledControl("Right stick", _rightStick);
		Grid.SetColumn(rightStickPanel, 1);
		Grid.SetRow(rightStickPanel, 1);
		root.Children.Add(rightStickPanel);

		var buttons = new WrapPanel
		{
			Margin = new Thickness(0, 18, 0, 0),
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		foreach (var control in new[]
		{
			ControllerElement.DPadUp,
			ControllerElement.DPadDown,
			ControllerElement.DPadLeft,
			ControllerElement.DPadRight,
			ControllerElement.A,
			ControllerElement.B,
			ControllerElement.X,
			ControllerElement.Y,
			ControllerElement.LeftShoulder,
			ControllerElement.RightShoulder,
			ControllerElement.Select,
			ControllerElement.Start,
			ControllerElement.Menu,
			ControllerElement.LeftStickButton,
			ControllerElement.RightStickButton
		})
		{
			buttons.Children.Add(CreateIndicator(control));
		}

		Grid.SetRow(buttons, 2);
		Grid.SetColumnSpan(buttons, 2);
		root.Children.Add(buttons);
		_stateText.Margin = new Thickness(0, 12, 0, 0);
		buttons.Children.Add(_stateText);
		return root;
	}

}
