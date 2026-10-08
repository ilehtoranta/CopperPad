using System.Reflection;
using CopperPad;

public sealed class DiagnosticsReaderLifetimeTests
{
	[Theory]
	[InlineData("profile", false)]
	[InlineData("profile", true)]
	[InlineData("device", false)]
	[InlineData("device", true)]
	[InlineData("stop", false)]
	[InlineData("stop", true)]
	[InlineData("dispose", false)]
	[InlineData("dispose", true)]
	public async Task SupersededReadersSuppressDelayedDataAndErrors(string action, bool fail)
	{
		using var provider = new DelayedProvider();
		using var host = new ControllerDiagnosticsHost(provider, new());
		var raw = new List<ControllerRawReportReceivedEventArgs>();
		var snapshots = new List<CopperControllerSnapshot>();
		host.RawReportReceived += (_, args) => raw.Add(args);
		host.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);
		host.Start();
		host.SelectDevice(provider.Devices[0].Id);
		await provider.First.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		var oldReader = ReaderTask(host);
		switch (action)
		{
			case "profile": host.UpdateProfiles(ControllerProfileSet.Empty); break;
			case "device": host.SelectDevice(provider.Devices[1].Id); break;
			case "stop": host.Stop(); break;
			case "dispose": host.Dispose(); break;
		}
		Assert.Equal(1, provider.First.DisposeCount);
		if (action is "profile" or "device")
		{
			await provider.Second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
			Assert.True(provider.PreviousHandleWasClosed);
		}
		if (fail) provider.First.Result.TrySetException(new IOException("Delayed old error"));
		else provider.First.Result.TrySetResult([255, 128, 128, 128, 0, 0, 1, 8]);
		await oldReader.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Empty(raw);
		Assert.Empty(snapshots);
		Assert.Equal(1, provider.First.DisposeCount);
	}

	[Fact]
	public async Task ProfileChangeDuringRawCallbackSuppressesRemainingRawAndMappedCallbacks()
	{
		using var provider = new DelayedProvider();
		using var host = new ControllerDiagnosticsHost(provider, new());
		var staleRaw = 0;
		var staleSnapshots = 0;
		host.RawReportReceived += (_, _) => host.UpdateProfiles(ControllerProfileSet.Empty);
		host.RawReportReceived += (_, _) => staleRaw++;
		host.SnapshotChanged += (_, _) => staleSnapshots++;
		host.Start();
		host.SelectDevice(provider.Devices[0].Id);
		await provider.First.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		var oldReader = ReaderTask(host);
		provider.First.Result.TrySetResult([128, 128, 128, 128, 0, 0, 1, 8]);
		await oldReader.WaitAsync(TimeSpan.FromSeconds(3));
		await provider.Second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Equal(0, staleRaw);
		Assert.Equal(0, staleSnapshots);
	}

	[Fact]
	public async Task CurrentReaderStillReportsErrors()
	{
		using var provider = new DelayedProvider();
		using var host = new ControllerDiagnosticsHost(provider, new());
		var error = new TaskCompletionSource<CopperControllerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
		host.SnapshotChanged += (_, args) => error.TrySetResult(args.Snapshot);
		host.Start();
		host.SelectDevice(provider.Devices[0].Id);
		await provider.First.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		provider.First.Result.TrySetException(new IOException("Current error"));
		var snapshot = await error.Task.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.False(snapshot.IsConnected);
		Assert.Contains("Current error", snapshot.Diagnostic);
	}

	private static Task ReaderTask(ControllerDiagnosticsHost host)
		=> (Task)typeof(ControllerDiagnosticsHost).GetField("_readerTask", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;

	private sealed class DelayedProvider : IHidDeviceProvider
	{
		private int _opens;
		public HidDeviceDescriptor[] Devices { get; } =
		[
			ControllerMapperTests.Device(0x1111, 0x2222, "First Gamepad", isGameControllerUsage: true),
			ControllerMapperTests.Device(0x1111, 0x3333, "Second Gamepad", isGameControllerUsage: true)
		];
		public DelayedStream First { get; } = new();
		public DelayedStream Second { get; } = new();
		public bool PreviousHandleWasClosed { get; private set; }
		public event EventHandler? Changed { add { } remove { } }
		public IReadOnlyList<HidDeviceDescriptor> GetDevices() => Devices;
		public IHidInputStream Open(HidDeviceDescriptor device, TimeSpan timeout)
		{
			if (Interlocked.Increment(ref _opens) == 1) return First;
			PreviousHandleWasClosed = First.DisposeCount == 1;
			return Second;
		}
		public void Dispose()
		{
			// The first completion is controlled by the test even after disposal.
			Second.Result.TrySetCanceled();
		}
	}

	private sealed class DelayedStream : IHidInputStream
	{
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<byte[]> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int MaxInputReportLength => 64;
		public int DisposeCount;
		public async ValueTask<int> ReadAsync(byte[] buffer, CancellationToken cancellationToken)
		{
			Entered.TrySetResult();
			// Model a native read whose completion races cancellation and stream closure.
			var bytes = await Result.Task;
			bytes.CopyTo(buffer, 0);
			return bytes.Length;
		}
		public void Dispose() => Interlocked.Increment(ref DisposeCount);
	}
}
