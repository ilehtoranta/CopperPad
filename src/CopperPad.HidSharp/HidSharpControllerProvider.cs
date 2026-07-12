/*
 * Copyright (C) 2026 Ilkka Lehtoranta
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;

namespace CopperPad;

/// <summary>
/// Desktop HID controller provider backed by HidSharp and SDL_GameControllerDB mappings.
/// </summary>
public sealed class HidSharpControllerProvider : IControllerProvider
{
	private readonly IHidDeviceProvider _provider;
	private readonly HidSharpControllerProviderOptions _options;
	private readonly object _gate = new();
	private readonly ConcurrentDictionary<string, CopperControllerSnapshot> _snapshots = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ControllerSession> _sessions = new(StringComparer.Ordinal);
	private bool _started;
	private bool _disposed;

	/// <summary>
	/// Creates a HidSharp controller provider using the local device list.
	/// </summary>
	/// <param name="options">Provider options, or <see langword="null"/> for defaults.</param>
	public HidSharpControllerProvider(HidSharpControllerProviderOptions? options = null)
		: this(new HidSharpDeviceProvider(), options ?? new HidSharpControllerProviderOptions())
	{
	}

	internal HidSharpControllerProvider(IHidDeviceProvider provider, HidSharpControllerProviderOptions options)
	{
		_provider = provider;
		_options = options;
		_provider.Changed += OnProviderChanged;
	}

	/// <inheritdoc />
	public event EventHandler<CopperControllersChangedEventArgs>? ControllersChanged;
	/// <inheritdoc />
	public event EventHandler<CopperControllerSnapshotChangedEventArgs>? SnapshotChanged;

	/// <inheritdoc />
	public void Start()
	{
		ThrowIfDisposed();
		var shouldScan = false;
		lock (_gate)
		{
			if (_started)
			{
				return;
			}

			_started = true;
			shouldScan = true;
		}

		if (shouldScan)
		{
			Rescan();
		}
	}

	/// <inheritdoc />
	public void Stop()
	{
		ControllerSession[] sessions;
		lock (_gate)
		{
			if (!_started)
			{
				return;
			}

			_started = false;
			sessions = _sessions.Values.ToArray();
			_sessions.Clear();
			_snapshots.Clear();
		}

		foreach (var session in sessions)
		{
			session.Dispose();
		}

		ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(Array.Empty<CopperControllerInfo>()));
	}

	/// <inheritdoc />
	public IReadOnlyList<CopperControllerInfo> GetControllers()
	{
		lock (_gate)
		{
			return _sessions.Values
				.Select(session => session.Info)
				.OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
	}

	/// <inheritdoc />
	public bool TryGetSnapshot(string controllerId, out CopperControllerSnapshot snapshot)
		=> _snapshots.TryGetValue(controllerId, out snapshot!);

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_provider.Changed -= OnProviderChanged;
		Stop();
		_provider.Dispose();
	}

	private void OnProviderChanged(object? sender, EventArgs args)
	{
		var shouldScan = false;
		lock (_gate)
		{
			shouldScan = _started;
		}

		if (shouldScan)
		{
			Rescan();
		}
	}

	private void Rescan()
	{
		var devices = _provider.GetDevices()
			.Where(device => ControllerMapperFactory.IsCandidate(device, _options.Profiles, _options.RequireGameControllerUsage))
			.GroupBy(device => device.Id, StringComparer.Ordinal)
			.Select(group => group.First())
			.ToArray();
		ControllerSession[] staleSessions;
		CopperControllerInfo[] controllers;
		lock (_gate)
		{
			if (!_started)
			{
				return;
			}

			var presentIds = devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
			var staleIds = _sessions.Keys.Where(id => !presentIds.Contains(id)).ToArray();
			staleSessions = staleIds.Select(id => _sessions[id]).ToArray();
			foreach (var stale in staleIds)
			{
				_sessions.Remove(stale);
				_snapshots.TryRemove(stale, out _);
			}

			foreach (var device in devices)
			{
				if (_sessions.ContainsKey(device.Id))
				{
					continue;
				}

				var mapper = ControllerMapperFactory.Create(device, _options.Profiles);
				var session = new ControllerSession(device, mapper, _provider, _options.ReadTimeout, PublishSnapshot);
				_sessions.Add(device.Id, session);
				_snapshots[device.Id] = CopperControllerSnapshotBuilder.Disconnected(device, DateTimeOffset.UtcNow, mapper.MappingInfo, device.Diagnostic);
				session.Start();
			}

			controllers = BuildControllersLocked();
		}

		foreach (var session in staleSessions)
		{
			session.Dispose();
		}

		ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(controllers));
	}

	private void PublishSnapshot(CopperControllerSnapshot snapshot)
	{
		bool connectionChanged;
		CopperControllerInfo[]? controllers = null;
		lock (_gate)
		{
			if (!_started || !_sessions.ContainsKey(snapshot.ControllerId))
			{
				return;
			}

			connectionChanged = !_snapshots.TryGetValue(snapshot.ControllerId, out var previous) || previous.IsConnected != snapshot.IsConnected;
			_snapshots[snapshot.ControllerId] = snapshot;
			if (connectionChanged)
			{
				controllers = BuildControllersLocked();
			}
		}

		SnapshotChanged?.Invoke(this, new CopperControllerSnapshotChangedEventArgs(snapshot));
		if (controllers != null)
		{
			ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(controllers));
		}
	}

	private CopperControllerInfo[] BuildControllersLocked()
		=> _sessions.Values
			.Select(session => session.Info)
			.OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, nameof(HidSharpControllerProvider));
	}

	private sealed class ControllerSession : IDisposable
	{
		private static readonly AsyncLocal<ControllerSession?> CurrentSession = new();
		private readonly HidDeviceDescriptor _device;
		private readonly IControllerMapper _mapper;
		private readonly IHidDeviceProvider _provider;
		private readonly TimeSpan _readTimeout;
		private readonly Action<CopperControllerSnapshot> _publish;
		private readonly CancellationTokenSource _cancellation = new();
		private readonly object _gate = new();
		private Task? _task;
		private IHidInputStream? _stream;
		private bool _disposed;

		public ControllerSession(
			HidDeviceDescriptor device,
			IControllerMapper mapper,
			IHidDeviceProvider provider,
			TimeSpan readTimeout,
			Action<CopperControllerSnapshot> publish)
		{
			_device = device;
			_mapper = mapper;
			_provider = provider;
			_readTimeout = readTimeout;
			_publish = publish;
			Info = CopperControllerSnapshotBuilder.ToInfo(device, true, mapper.MappingInfo, device.Diagnostic);
		}

		public CopperControllerInfo Info { get; private set; }

		public void Start() => _task = Task.Run(ReadLoopAsync);

		public void Dispose()
		{
			Task? task;
			lock (_gate)
			{
				if (_disposed)
				{
					return;
				}

				_disposed = true;
				_cancellation.Cancel();
				_stream?.Dispose();
				task = _task;
			}

			if (task == null)
			{
				_cancellation.Dispose();
				return;
			}

			if (ReferenceEquals(CurrentSession.Value, this) || !WaitForCompletion(task))
			{
				_ = task?.ContinueWith(
					_ => _cancellation.Dispose(),
					CancellationToken.None,
					TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
				return;
			}

			_cancellation.Dispose();
		}

		private static bool WaitForCompletion(Task task)
		{
			try
			{
				return task.Wait(TimeSpan.FromSeconds(1));
			}
			catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
			{
				return true;
			}
		}

		private async Task ReadLoopAsync()
		{
			CurrentSession.Value = this;
			var backoff = TimeSpan.FromMilliseconds(250);
			var disconnectedPublished = false;
			while (!_cancellation.IsCancellationRequested)
			{
				try
				{
					using var stream = _provider.Open(_device, _readTimeout);
					lock (_gate)
					{
						_stream = stream;
					}

					var buffer = new byte[Math.Max(1, stream.MaxInputReportLength)];
					while (!_cancellation.IsCancellationRequested)
					{
						var read = await stream.ReadAsync(buffer, _cancellation.Token).ConfigureAwait(false);
						if (read <= 0)
						{
							continue;
						}

						var snapshot = new byte[read];
						Array.Copy(buffer, snapshot, read);
						var input = new RawControllerInput(_device, snapshot, read, DateTimeOffset.UtcNow);
						var mapped = _mapper.Map(input);
						Info = CopperControllerSnapshotBuilder.ToInfo(_device, true, _mapper.MappingInfo, mapped.Diagnostic);
						disconnectedPublished = false;
						backoff = TimeSpan.FromMilliseconds(250);
						_publish(mapped);
					}
				}
				catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
				{
					break;
				}
				catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or NotSupportedException)
				{
					if (!disconnectedPublished)
					{
						var diagnostic = "HID read failed: " + ex.Message;
						Info = CopperControllerSnapshotBuilder.ToInfo(_device, false, _mapper.MappingInfo, diagnostic);
						_publish(CopperControllerSnapshotBuilder.Disconnected(_device, DateTimeOffset.UtcNow, _mapper.MappingInfo, diagnostic));
						disconnectedPublished = true;
					}

					await Task.Delay(backoff, _cancellation.Token).ConfigureAwait(false);
					backoff = TimeSpan.FromMilliseconds(Math.Min(5000, backoff.TotalMilliseconds * 2));
				}
				finally
				{
					lock (_gate)
					{
						_stream = null;
					}
				}
			}

			CurrentSession.Value = null;
		}
	}
}
