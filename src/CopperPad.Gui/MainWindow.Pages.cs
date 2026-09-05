using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using CopperPad;

namespace CopperPad.Gui;

internal sealed partial class MainWindow
{
    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(20, 16) };
        var brand = new TextBlock { Text = "COPPERPAD  /  Controller studio", FontSize = 16, Foreground = CopperTheme.Copper, Margin = new Thickness(0, 0, 0, 16) };
        root.Children.Add(brand);
        var body = new Grid { ColumnDefinitions = new("250,12,*") };
        body.ColumnDefinitions[0].MinWidth = 180;
        body.ColumnDefinitions[0].MaxWidth = 330;
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        body.Children.Add(BuildDevicePane());
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var workspace = BuildWorkspace(); Grid.SetColumn(workspace, 2); body.Children.Add(workspace);
        var save = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 12, 0, 4) };
        save.Children.Add(_saveState);
        _saveState.VerticalAlignment = VerticalAlignment.Center;
        _saveProfileButton.Content = "Save changes";
        _saveProfileButton.Classes.Add("primary");
        _saveProfileButton.Click += async (_, _) => await SaveDraftProfileAsync();
        _revertButton.Click += (_, _) => { if (_session == null) return; StopGuidedMapping("Changes reverted."); _session.Revert(); RestoreSession(); };
        var actions = Actions(_revertButton, _saveProfileButton);
        Grid.SetColumn(actions, 1); save.Children.Add(actions);
        Grid.SetRow(save, 2); root.Children.Add(save);
        _statusText.Foreground = CopperTheme.Muted;
        Grid.SetRow(_statusText, 3); root.Children.Add(_statusText);
        return root;
    }

    private Control BuildDevicePane()
    {
        var pane = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), MinWidth = 180 };
        pane.Children.Add(Heading("Controllers", 18));
        _deviceFilterText.Margin = new Thickness(0, 8, 0, 12);
        _deviceFilterText.Foreground = CopperTheme.Muted;
        Grid.SetRow(_deviceFilterText, 1); pane.Children.Add(_deviceFilterText);
        _deviceList.SelectionChanged += (_, _) => { if (_deviceList.SelectedItem is DeviceListItem item) SelectDevice(item.Device); };
        _deviceList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<DeviceListItem>((item, _) =>
            new TextBlock { Text = item?.Device.ProductName, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 10), MaxWidth = 210 });
        Grid.SetRow(_deviceList, 2); pane.Children.Add(_deviceList);
        _showAllDevicesCheck.Content = "Show all HID devices";
        _showAllDevicesCheck.IsCheckedChanged += (_, _) => ApplyDeviceFilter();
        var options = new StackPanel { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        options.Children.Add(Button("Refresh devices", (_, _) => RefreshDevices()));
        options.Children.Add(new Expander { Header = "Device & profile options", Content = new StackPanel { Spacing = 8, Children = {
            _showAllDevicesCheck,
            Button("Import profiles…", async (_, _) => await ImportProfilesAsync()),
            Button("Export saved profiles…", async (_, _) => await ExportProfilesAsync())
        } } });
        Grid.SetRow(options, 3); pane.Children.Add(options);
        return pane;
    }

    private Control BuildWorkspace()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,*"), MinWidth = 360 };
        var header = new StackPanel { Spacing = 6, Margin = new Thickness(12, 0, 0, 14) };
        _deviceTitle.FontSize = 25; _deviceTitle.FontWeight = FontWeight.SemiBold;
        _deviceSubtitle.Foreground = CopperTheme.Muted;
        header.Children.Add(_deviceTitle); header.Children.Add(_deviceSubtitle);
        _setupButton.Click += (_, _) => { _tabs.SelectedIndex = 1; SetStatus("Start guided mapping to assign your controller's inputs."); };
        header.Children.Add(_setupButton); root.Children.Add(header);
        _previewText.Margin = new Thickness(12, 0, 0, 8); _previewText.Foreground = CopperTheme.Copper;
        Grid.SetRow(_previewText, 1); root.Children.Add(_previewText);
        _tabs.ItemsSource = new[] {
            new TabItem { Header = "Test", Content = BuildTestTab() },
            new TabItem { Header = "Mapping", Content = BuildMapTab() },
            new TabItem { Header = "Calibration", Content = BuildCalibrationTab() },
            new TabItem { Header = "Diagnostics", Content = BuildReportsTab() }
        };
        _tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != _tabs) return;
            if (_session != null) _session.SelectedPage = _tabs.SelectedIndex;
            if (_tabs.SelectedIndex != 1 && _guidedMappingActive) StopGuidedMapping("Completed assignments retained. Review them in Test.");
            if (_tabs.SelectedIndex != 2 && (_rangeReview || _calibrationActive))
            {
                _rangeReview = false;
                LoadCalibrationFromTarget();
                UpdateEditorState();
                SchedulePreview();
            }
        };
        Grid.SetRow(_tabs, 2); root.Children.Add(_tabs);
        return root;
    }

    private Control BuildTestTab()
    {
        var panel = Page("Test your controller", "Press buttons and move the sticks. Live feedback appears below.");
        var toggle = new CheckBox { Content = "Use labeled control grid" };
        var grid = BuildControlGrid(); grid.IsVisible = false;
        toggle.IsCheckedChanged += (_, _) => { grid.IsVisible = toggle.IsChecked == true; _gamepad.IsVisible = toggle.IsChecked != true; };
        panel.Children.Add(toggle); panel.Children.Add(_liveNumbers); panel.Children.Add(Card(_gamepad)); panel.Children.Add(grid);
        return Scroll(panel);
    }

    private Control BuildMapTab()
    {
        var panel = Page("Map your controls", "Assign physical inputs to controller actions. Unassigned controls are optional.");
        _profileName.PlaceholderText = "Profile name";
        _profileName.TextChanged += (_, _) => { if (_loadingEditor || _draftProfile == null) return; _draftProfile = _draftProfile with { Name = _profileName.Text ?? "" }; CommitEditorChange(); };
        panel.Children.Add(LabeledControl("Profile name", _profileName));
        _guidedPromptText.Text = "Release all controls, then start guided mapping.";
        _guidedPromptText.FontSize = 19;
        _guidedProgress.Foreground = CopperTheme.Copper;
        _startGuidedMappingButton.Classes.Add("primary");
        _startGuidedMappingButton.Click += (_, _) => StartGuidedMapping();
        _skipGuidedMappingButton.Click += (_, _) => SkipGuidedTarget();
        _stopGuidedMappingButton.Click += (_, _) => StopGuidedMapping("Setup stopped. Completed assignments are retained.");
        _backGuided.Click += (_, _) => RestartGuidedTarget(Math.Max(0, _guidedTargetIndex - 1));
        _retryGuided.Click += (_, _) => RestartGuidedTarget(_guidedTargetIndex);
        var guided = new StackPanel { Spacing = 12, Children = { _guidedProgress, _guidedPromptText,
            Actions(_startGuidedMappingButton, _backGuided, _retryGuided, _skipGuidedMappingButton, _stopGuidedMappingButton),
            Button("Review in Test →", (_, _) => { StopGuidedMapping("Review your assignments in Test."); _tabs.SelectedIndex = 0; }) } };
        panel.Children.Add(Card(guided));
        _undoButton.Click += (_, _) => UndoEdit(false); _redoButton.Click += (_, _) => UndoEdit(true);
        panel.Children.Add(Actions(_undoButton, _redoButton, Button("Clear all bindings", (_, _) => ClearBindings()), Button("Advanced editor", (_, _) => { _advancedEditor.IsExpanded = true; _advancedEditor.BringIntoView(); })));
        panel.Children.Add(_mappingRows);
        _targetBox.ItemsSource = MappingTargetItem.All; _targetBox.SelectedIndex = 0;
        _targetBox.SelectionChanged += (_, _) => PopulateFieldsFromSelectedTarget();
        _sourceKindBox.ItemsSource = Enum.GetValues<ControllerBindingSourceKind>();
        _sourceKindBox.SelectedItem = ControllerBindingSourceKind.ReportBit;
        _sourceKindBox.SelectionChanged += (_, _) => UpdateSourceFieldAvailability();
        _useSuggestionButton.Content = "Assign detected input";
        _useSuggestionButton.Click += (_, _) => { if (_suggestedSource != null) { SetSourceFields(_suggestedSource); AddOrUpdateBinding(); } };
        _ignoreSuggestionButton.Click += (_, _) => IgnoreSuggestedSource();
        var fields = new WrapPanel();
        foreach (var field in new[] { LabeledControl("Control", _targetBox), LabeledControl("Source kind", _sourceKindBox), LabeledControl("Byte offset", _offsetBox), LabeledControl("Bit", _bitBox), LabeledControl("Hat value", _hatBox) }) fields.Children.Add(field);
        var advanced = new StackPanel { Spacing = 12, Children = { fields, _sourceInvertCheck,
            Actions(Button("Capture baseline", (_, _) => CaptureBaseline()), _useSuggestionButton, _ignoreSuggestionButton, Button("Assign input", (_, _) => AddOrUpdateBinding())), _captureStatusText } };
        _advancedEditor.Content = advanced;
        panel.Children.Add(_advancedEditor);
        _validationText.Foreground = CopperTheme.Warning;
        panel.Children.Add(_validationText);
        UpdateSourceFieldAvailability();
        return Scroll(panel);
    }

    private Control BuildCalibrationTab()
    {
        var panel = Page("Calibrate movement", "Tune a mapped axis. Raw sources must be assigned in Mapping before calibration.");
        _calibrationTargetBox.ItemsSource = AxisTargets.Select(x => new MappingTargetItem(x)).ToArray();
        _calibrationTargetBox.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MappingTargetItem>((item, _) => Text(item == null ? "" : Friendly(item.Element)));
        _calibrationTargetBox.SelectedIndex = 0;
        _calibrationTargetBox.SelectionChanged += (_, _) => { _rangeReview = false; LoadCalibrationFromTarget(); UpdateEditorState(); SchedulePreview(); };
        panel.Children.Add(LabeledControl("Axis / trigger", _calibrationTargetBox));
        panel.Children.Add(_calibrationValues);
        panel.Children.Add(Card(_calibrationGraph));
        panel.Children.Add(Card(new StackPanel { Spacing = 8, Children = {
            Heading("Range calibration", 17),
            Text("Release the control, capture rest, then move through its full range."),
            Actions(Button("1 · Capture rest", (_, _) => { StartCalibrationCapture(); CaptureCalibrationCenter(); StopCalibrationCapture(); _rangeReview = GetCalibrationBinding() != null; _calibrationStep.Text = "2 · Start range capture and move through the full range."; UpdateEditorState(); }),
                Button("2 · Start range", (_, _) => { StartCalibrationCapture(); _rangeReview = GetCalibrationBinding() != null; _calibrationStep.Text = "Move through the full range. Recording…"; UpdateEditorState(); }),
                Button("3 · Review", (_, _) => { StopCalibrationCapture(); _calibrationStep.Text = "Review the range below, then accept or cancel."; })),
            _calibrationStep,
            Actions(Button("Accept range", (_, _) => AcceptCalibrationRange()), Button("Cancel capture", (_, _) => { _rangeReview = false; LoadCalibrationFromTarget(); SchedulePreview(); UpdateEditorState(); _calibrationStep.Text = "Capture cancelled."; }))
        } }));
        _deadzoneSlider.Width = 220; _saturationSlider.Width = 220;
        _deadzoneSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) CalibrationSettingChanged(); };
        _saturationSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) CalibrationSettingChanged(); };
        _invertCheck.IsCheckedChanged += (_, _) => CalibrationSettingChanged();
        _deadzoneNumber.ValueChanged += (_, _) => { if (!_loadingCalibration) _deadzoneSlider.Value = (double)(_deadzoneNumber.Value ?? 0); };
        _saturationNumber.ValueChanged += (_, _) => { if (!_loadingCalibration) _saturationSlider.Value = (double)(_saturationNumber.Value ?? 1); };
        panel.Children.Add(LabeledControl("Deadzone · Ignore small movements around rest", Actions(_deadzoneSlider, _deadzoneNumber)));
        panel.Children.Add(LabeledControl("Saturation · Reach full output before the physical limit", Actions(_saturationSlider, _saturationNumber)));
        panel.Children.Add(Actions(_invertCheck, Button("Reset this axis", (_, _) => ResetCalibration())));
        panel.Children.Add(_calibrationStatusText); panel.Children.Add(_calibrationPreviewText);
        return Scroll(panel);
    }

    private Control BuildReportsTab()
    {
        var panel = Page("Diagnostics", "Inspect the selected device's HID reports and descriptor.");
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
            _mappingRows.Children.Add(Heading(group.Key, 17));
            foreach (var target in group)
            {
                var binding = _draftProfile?.Bindings.FirstOrDefault(x => x.Target == target);
                var row = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 3) };
                var label = new StackPanel { Spacing = 3, Children = { Text(Friendly(target)), new TextBlock { Text = binding == null ? "Unassigned · optional" : ReportAnalyzer.FormatSource(binding.Source), Foreground = binding == null ? CopperTheme.Muted : CopperTheme.Success, TextWrapping = TextWrapping.Wrap } } };
                if (binding != null && _draftProfile != null && _selectedDevice != null)
                {
                    var issues = ProfileEditor.ValidateProfile(_draftProfile with { Bindings = new[] { binding } }, _selectedDevice.MaxInputReportLength);
                    foreach (var issue in issues) label.Children.Add(new TextBlock { Text = issue.Message, Foreground = CopperTheme.Warning, TextWrapping = TextWrapping.Wrap });
                }
                row.Children.Add(label);
                var remap = Button("Remap", (_, _) => { SetSelectedTarget(target); StartGuidedMapping(); if (_guidedMappingActive) RestartGuidedTarget(ProfileEditor.MappableTargets.ToList().IndexOf(target)); });
                remap.IsEnabled = _selectedDevice != null;
                var clear = Button("Clear", (_, _) => { SetSelectedTarget(target); RemoveSelectedBinding(); }); clear.IsEnabled = binding != null;
                var actions = Actions(remap, clear); Grid.SetColumn(actions, 1); row.Children.Add(actions);
                var card = Card(row);
                if (_guidedMappingActive && target == ProfileEditor.MappableTargets[_guidedTargetIndex]) card.BorderBrush = CopperTheme.Copper;
                _mappingRows.Children.Add(card);
            }
        }
    }

    private static string Friendly(ControllerElement target) => target switch {
        ControllerElement.LeftStickX => "Left stick · Horizontal", ControllerElement.LeftStickY => "Left stick · Vertical",
        ControllerElement.RightStickX => "Right stick · Horizontal", ControllerElement.RightStickY => "Right stick · Vertical",
        _ => new MappingTargetItem(target).ToString()
    };
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Heading(string value, double size) => new() { Text = value, FontSize = size, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    private static StackPanel Page(string title, string help) => new() { Spacing = 10, Margin = new Thickness(12), Children = { Heading(title, 20), new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = CopperTheme.Muted } } };
    private static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static Border Card(Control content) => new() { Background = CopperTheme.Surface, BorderBrush = CopperTheme.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = content };
    private static WrapPanel Actions(params Control[] controls)
    {
        var result = new WrapPanel();
        foreach (var control in controls) { control.Margin = new Thickness(0, 0, 8, 6); result.Children.Add(control); }
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
