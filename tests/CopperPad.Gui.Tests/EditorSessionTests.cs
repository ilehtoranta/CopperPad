using CopperPad;
using CopperPad.Gui;

public sealed class EditorSessionTests
{
    [Fact]
    public void NewSessionIsCleanAndClearBindingsCanBeUndoneAndRedone()
    {
        var session = new EditorSession(WorkspaceTests.Device(), null);
        Assert.False(session.IsDirty);
        var bound = ProfileEditor.UpsertBinding(session.Draft, ProfileEditor.CreateBinding(ControllerElement.South, new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportBit, Offset = 0, Bit = 0 }, null));
        session.Edit(bound);
        session.Edit(bound with { Bindings = [] });
        session.Undo(); Assert.Single(session.Draft.Bindings);
        session.Redo(); Assert.Empty(session.Draft.Bindings);
        session.Revert(); Assert.False(session.IsDirty); Assert.False(session.CanUndo);
    }

    [Fact]
    public void PreviewIsDeviceScopedAndKeepsSavedProfilePrecedenceIntact()
    {
        var device = WorkspaceTests.Device();
        var saved = ProfileEditor.CreateDefaultProfile(device, DateTimeOffset.UtcNow);
        var profiles = new ControllerProfileSet { Profiles = [saved] };
        var session = new EditorSession(device, saved);
        session.Edit(saved with { Name = "Draft" });
        var preview = session.Preview(profiles);
        Assert.Equal(device.Id, preview.Profiles[0].DeviceId);
        Assert.Same(saved, preview.Profiles[1]);
        Assert.Same(saved, profiles.Profiles[0]);
        Assert.Null(session.Draft.DeviceId);
    }

    [Fact]
    public void RenamingDeviceSpecificProfileReplacesBaselineInPlace()
    {
        var saved = ProfileEditor.CreateDefaultProfile(WorkspaceTests.Device(), DateTimeOffset.UtcNow) with { DeviceId = "test-pad" };
        var profiles = new ControllerProfileSet { Profiles = [saved] };
        var session = new EditorSession(WorkspaceTests.Device(), saved);
        session.Edit(saved with { Name = "Renamed" });
        var result = session.WithSavedDraft(profiles, session.Draft);
        Assert.Equal("Renamed", Assert.Single(result.Profiles).Name);
        session.MarkSaved(session.Draft);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void CalibrationCaptureStartsFreshInsteadOfKeepingOldExtremes()
    {
        var capture = new AxisCalibrationCapture();
        capture.CaptureCenter(128);
        capture.Observe(20); capture.Observe(230);
        var axis = capture.ToCalibration(false, .12, .9);
        Assert.Equal(20, axis.Minimum); Assert.Equal(230, axis.Maximum); Assert.Equal(128, axis.Center);
    }
}
