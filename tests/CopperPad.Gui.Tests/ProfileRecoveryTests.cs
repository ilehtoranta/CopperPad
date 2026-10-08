using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CopperPad;
using CopperPad.Gui;

public sealed class ProfileRecoveryTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
    private static ControllerProfileSet Replacement => new() { Profiles = [new ControllerProfile { Name = "Recovered" }] };

    [Fact]
    public async Task CancelledLoadRetainsSaveProtection()
    {
        var path = NewPath(); var store = new FileControllerProfileStore(path);
        await store.SaveAsync(Replacement);
        var original = await File.ReadAllBytesAsync(path);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.LoadAsync(cancellation.Token));
            Assert.True(store.HasLoadFailure);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(ControllerProfileSet.Empty));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("{broken document")]
    [InlineData("{\"schemaVersion\":3,\"profiles\":[]}")]
    public async Task FailedLoadBlocksOrdinarySaveAndImportPreservesOriginal(string original)
    {
        var path = NewPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);
        var store = new FileControllerProfileStore(path);
        try
        {
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await store.LoadAsync());
            Assert.True(store.HasLoadFailure);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(Replacement));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            await store.SaveReplacementAsync(Replacement);
            Assert.False(store.HasLoadFailure);
            Assert.NotNull(store.LastBackupPath);
            Assert.Equal(original, await File.ReadAllTextAsync(store.LastBackupPath!));
            Assert.Equal("Recovered", Assert.Single((await store.LoadAsync()).Profiles).Name);
        }
        finally { File.Delete(path); if (store.LastBackupPath != null) File.Delete(store.LastBackupPath); }
    }

    [Fact]
    public async Task TransientReadFailureRequiresSuccessfulReloadBeforeSave()
    {
        var path = NewPath(); var store = new FileControllerProfileStore(path);
        await store.SaveAsync(Replacement);
        try
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAnyAsync<IOException>(async () => await store.LoadAsync());
                Assert.True(store.HasLoadFailure);
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(ControllerProfileSet.Empty));
                await Assert.ThrowsAnyAsync<IOException>(async () => await store.SaveReplacementAsync(ControllerProfileSet.Empty));
                Assert.True(store.HasLoadFailure);
            }
            Assert.True(store.HasLoadFailure);
            Assert.Equal("Recovered", Assert.Single((await store.LoadAsync()).Profiles).Name);
            Assert.False(store.HasLoadFailure);
            await store.SaveAsync(ControllerProfileSet.Empty);
            Assert.Empty((await store.LoadAsync()).Profiles);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FailedReplacementRetainsOriginalAndLoadFailure()
    {
        var path = NewPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "broken");
        var store = new FileControllerProfileStore(path);
        try
        {
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await store.LoadAsync());
            var invalid = new ControllerProfileSet { Profiles = [new ControllerProfile { Name = "" }] };
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(async () => await store.SaveReplacementAsync(invalid));
            Assert.True(store.HasLoadFailure);
            Assert.Equal("broken", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }
}

public sealed partial class WorkspaceTests
{
    [Fact]
    public async Task RepairAndRetryLoadingUnblocksSaveWithoutDiscardingTheDraft()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "broken");
            var host = new FakeGuiService(); using var window = Open(host, path);
            var store = Field<FileControllerProfileStore>(window, "_profileStore");
            for (int i = 0; i < 40 && !store.HasLoadFailure; i++) await Task.Delay(25);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            var draft = Field<EditorSession>(window, "_session");
            await new FileControllerProfileStore(path).SaveAsync(new() { Profiles = [new() { Name = "Other device", VendorId = 0xabcd }] });
            await CallAsync(window, "RetryLoadProfilesAsync");
            Assert.False(store.HasLoadFailure);
            Assert.Same(draft, Field<EditorSession>(window, "_session"));
            Assert.True(draft.IsDirty);
            Assert.True(Field<Button>(window, "_saveProfileButton").IsEnabled);
            await CallAsync(window, "SaveDraftProfileAsync");
            var saved = await store.LoadAsync();
            Assert.Equal(2, saved.Profiles.Count);
            Assert.Contains(saved.Profiles, p => p.Name == "Other device");
            Assert.False(draft.IsDirty);
            File.Delete(path);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void HeadlessSessionStartupAndTeardownCanRepeat()
    {
        // Avalonia 12.0.3 could capture a null dispatch task during Task.Run startup.
        for (var i = 0; i < 100; i++)
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        }
    }

    [Theory]
    [InlineData("{broken document")]
    [InlineData("{\"schemaVersion\":3,\"profiles\":[]}")]
    public async Task FailedLoadBlocksSaveButtonAndKeyboardSaveAndSupportsBackupImport(string original)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(GuiTestApplication));
        await session.Dispatch(async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "CopperPad-tests", Guid.NewGuid() + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, original);
            var host = new FakeGuiService(); using var window = Open(host, path);
            var store = Field<FileControllerProfileStore>(window, "_profileStore");
            for (int i = 0; i < 40 && !store.HasLoadFailure; i++) await Task.Delay(25);
            Assert.True(store.HasLoadFailure);
            host.Devices(Device()); Dispatcher.UIThread.RunJobs(); SeedBinding(window);
            Assert.False(Field<Button>(window, "_saveProfileButton").IsEnabled);
            await CallAsync(window, "SaveDraftProfileAsync");
            Assert.True(Field<EditorSession>(window, "_session").IsDirty);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            window.DialogHandler = (title, _, _) => Task.FromResult<string?>(title.StartsWith("Replace") ? "Replace" : "Discard");
            await CallAsync(window, "ImportDocumentAsync", ControllerProfileSet.Empty);
            Assert.False(store.HasLoadFailure);
            Assert.Equal(original, await File.ReadAllTextAsync(store.LastBackupPath!));
            Assert.Empty((await store.LoadAsync()).Profiles);
            File.Delete(path); File.Delete(store.LastBackupPath!);
            return true;
        }, CancellationToken.None);
    }
}
