using System.Text.Json;
using CopperPad;

namespace CopperPad.Gui;

internal sealed class EditorSession(HidDeviceInfo device, ControllerProfile? saved)
{
    private readonly Stack<ControllerProfile> _undo = new();
    private readonly Stack<ControllerProfile> _redo = new();
    public HidDeviceInfo Device { get; set; } = device;
    public ControllerProfile Baseline { get; private set; } = saved ?? ProfileEditor.CreateDefaultProfile(device, DateTimeOffset.UtcNow);
    public ControllerProfile Draft { get; private set; } = saved ?? ProfileEditor.CreateDefaultProfile(device, DateTimeOffset.UtcNow);
    public bool HasSavedProfile { get; private set; } = saved != null;
    public int SelectedPage { get; set; }
    public bool Connected { get; set; } = true;
    public bool IsDirty => !Equivalent(Baseline, Draft);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyList<ProfileValidationIssue> Issues => Draft.Bindings.Count == 0
        ? ProfileEditor.ValidateProfile(Draft, Device.MaxInputReportLength).Append(new ProfileValidationIssue("Assign at least one control before saving a custom mapping.")).ToArray()
        : ProfileEditor.ValidateProfile(Draft, Device.MaxInputReportLength);
    public void Edit(ControllerProfile profile)
    {
        if (Equivalent(Draft, profile)) return;
        _undo.Push(Draft);
        _redo.Clear();
        Draft = profile;
    }
    public void Undo() { if (_undo.TryPop(out var value)) { _redo.Push(Draft); Draft = value; } }
    public void Redo() { if (_redo.TryPop(out var value)) { _undo.Push(Draft); Draft = value; } }
    public void Revert() { Draft = Baseline; _undo.Clear(); _redo.Clear(); }
    public void MarkSaved(ControllerProfile profile) { Baseline = Draft = profile; HasSavedProfile = true; _undo.Clear(); _redo.Clear(); }
    public ControllerProfileSet WithSavedDraft(ControllerProfileSet profiles, ControllerProfile draft)
    {
        var list = profiles.Profiles.ToArray();
        var index = Array.FindIndex(list, p => ReferenceEquals(p, Baseline));
        if (index < 0) return ProfileEditor.MergeProfile(profiles, draft);
        list[index] = draft;
        return profiles with { Profiles = list };
    }
    public ControllerProfileSet Preview(ControllerProfileSet savedProfiles) => Issues.Count > 0 ? savedProfiles : savedProfiles with
    {
        Profiles = new[] { Draft with { DeviceId = Device.Id } }.Concat(savedProfiles.Profiles).ToArray()
    };
    private static bool Equivalent(ControllerProfile a, ControllerProfile b) =>
        JsonSerializer.Serialize(a with { CreatedAt = null, UpdatedAt = null }) == JsonSerializer.Serialize(b with { CreatedAt = null, UpdatedAt = null });
}
