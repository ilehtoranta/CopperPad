#if IOS || MACCATALYST
using Foundation;
using GameController;

namespace CopperPad;

/// <summary>
/// Apple GameController provider for iOS and Mac Catalyst targets.
/// </summary>
public sealed class GameControllerControllerProvider : IControllerProvider
{
	private readonly object _gate = new();
	private readonly Dictionary<GCController, ControllerSession> _sessions = new(ReferenceEqualityComparer.Instance);
	private readonly Dictionary<string, CopperControllerSnapshot> _snapshots = new(StringComparer.Ordinal);
	private NSObject? _connectedObserver;
	private NSObject? _disconnectedObserver;
	private bool _started;
	private bool _disposed;

	/// <inheritdoc />
	public event EventHandler<CopperControllersChangedEventArgs>? ControllersChanged;
	/// <inheritdoc />
	public event EventHandler<CopperControllerSnapshotChangedEventArgs>? SnapshotChanged;

	/// <inheritdoc />
	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_started)
			{
				return;
			}

			_started = true;
			_connectedObserver = GCController.Notifications.ObserveDidConnect((_, _) => SynchronizeControllers());
			_disconnectedObserver = GCController.Notifications.ObserveDidDisconnect((_, _) => SynchronizeControllers());
		}

		SynchronizeControllers();
	}

	/// <inheritdoc />
	public void Stop()
	{
		ControllerSession[] sessions;
		NSObject? connectedObserver;
		NSObject? disconnectedObserver;
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
			connectedObserver = _connectedObserver;
			disconnectedObserver = _disconnectedObserver;
			_connectedObserver = null;
			_disconnectedObserver = null;
		}

		foreach (var session in sessions)
		{
			session.Detach();
		}

		connectedObserver?.Dispose();
		disconnectedObserver?.Dispose();
		ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(Array.Empty<CopperControllerInfo>()));
	}

	/// <inheritdoc />
	public IReadOnlyList<CopperControllerInfo> GetControllers()
	{
		lock (_gate)
		{
			return _sessions.Values.Select(session => session.Info).ToArray();
		}
	}

	/// <inheritdoc />
	public bool TryGetSnapshot(string controllerId, out CopperControllerSnapshot snapshot)
	{
		lock (_gate)
		{
			return _snapshots.TryGetValue(controllerId, out snapshot!);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		Stop();
	}

	private void SynchronizeControllers()
	{
		var current = GCController.Controllers
			.OfType<GCController>()
			.ToHashSet<GCController>(ReferenceEqualityComparer.Instance);
		var disconnected = new List<(ControllerSession Session, CopperControllerSnapshot Snapshot)>();
		var connected = new List<ControllerSession>();
		CopperControllerInfo[] infos;
		lock (_gate)
		{
			if (!_started)
			{
				return;
			}

			foreach (var stale in _sessions.Keys.Where(controller => !current.Contains(controller)).ToArray())
			{
				var session = _sessions[stale];
				var snapshot = session.CreateSnapshot(connected: false);
				disconnected.Add((session, snapshot));
				_sessions.Remove(stale);
				_snapshots.Remove(session.Id);
			}

			foreach (var controller in current)
			{
				if (_sessions.ContainsKey(controller))
				{
					continue;
				}

				var session = new ControllerSession(controller, PublishSessionSnapshot);
				_sessions.Add(controller, session);
				var snapshot = session.CreateSnapshot(connected: true);
				_snapshots[session.Id] = snapshot;
				connected.Add(session);
			}

			infos = _sessions.Values.Select(session => session.Info).ToArray();
		}

		foreach (var item in disconnected)
		{
			item.Session.Detach();
			SnapshotChanged?.Invoke(this, new CopperControllerSnapshotChangedEventArgs(item.Snapshot));
		}

		foreach (var session in connected)
		{
			session.Attach();
			SnapshotChanged?.Invoke(this, new CopperControllerSnapshotChangedEventArgs(session.CreateSnapshot(connected: true)));
		}

		ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(infos));
	}

	private void PublishSessionSnapshot(ControllerSession session)
	{
		var snapshot = session.CreateSnapshot(connected: true);
		lock (_gate)
		{
			if (!_started || !_sessions.TryGetValue(session.Controller, out var current) || !ReferenceEquals(current, session))
			{
				return;
			}

			_snapshots[session.Id] = snapshot;
		}

		SnapshotChanged?.Invoke(this, new CopperControllerSnapshotChangedEventArgs(snapshot));
	}

	// GCGamepad remains supported for older standard-profile controllers even though
	// Apple now recommends GCExtendedGamepad for newly manufactured controllers.
#pragma warning disable CA1422
	private sealed class ControllerSession(GCController controller, Action<ControllerSession> publish)
	{
		public GCController Controller { get; } = controller;
		public string Id { get; } = "apple:" + Guid.NewGuid().ToString("N");
		public string Name => string.IsNullOrWhiteSpace(Controller.VendorName) ? "GameController" : Controller.VendorName;
		public bool IsExtended => Controller.ExtendedGamepad != null;
		public CopperControllerInfo Info => GameControllerSnapshotFactory.CreateInfo(Id, Name, connected: true, IsExtended);

		public void Attach()
		{
			if (Controller.ExtendedGamepad is { } extended)
			{
				extended.ValueChangedHandler = (_, _) => publish(this);
			}
			else if (Controller.Gamepad is { } gamepad)
			{
				gamepad.ValueChangedHandler = (_, _) => publish(this);
			}
			else if (Controller.MicroGamepad is { } micro)
			{
				micro.ValueChangedHandler = (_, _) => publish(this);
			}
		}

		public void Detach()
		{
			if (Controller.ExtendedGamepad is { } extended) extended.ValueChangedHandler = null;
			if (Controller.Gamepad is { } gamepad) gamepad.ValueChangedHandler = null;
			if (Controller.MicroGamepad is { } micro) micro.ValueChangedHandler = null;
		}

		public CopperControllerSnapshot CreateSnapshot(bool connected)
			=> GameControllerSnapshotFactory.CreateSnapshot(Id, Name, connected, IsExtended, ReadState(), DateTimeOffset.UtcNow);

		private GameControllerState ReadState()
		{
			if (Controller.ExtendedGamepad is { } extended)
			{
				return new GameControllerState
				{
					South = extended.ButtonA.IsPressed,
					East = extended.ButtonB.IsPressed,
					West = extended.ButtonX.IsPressed,
					North = extended.ButtonY.IsPressed,
					LeftShoulder = extended.LeftShoulder.IsPressed,
					RightShoulder = extended.RightShoulder.IsPressed,
					Select = extended.ButtonOptions?.IsPressed == true,
					Start = extended.ButtonMenu.IsPressed,
					Menu = extended.ButtonHome?.IsPressed == true,
					LeftStickButton = extended.LeftThumbstickButton?.IsPressed == true,
					RightStickButton = extended.RightThumbstickButton?.IsPressed == true,
					DPadUp = extended.DPad.Up.IsPressed,
					DPadDown = extended.DPad.Down.IsPressed,
					DPadLeft = extended.DPad.Left.IsPressed,
					DPadRight = extended.DPad.Right.IsPressed,
					LeftStickX = extended.LeftThumbstick.XAxis.Value,
					LeftStickY = extended.LeftThumbstick.YAxis.Value,
					RightStickX = extended.RightThumbstick.XAxis.Value,
					RightStickY = extended.RightThumbstick.YAxis.Value,
					LeftTrigger = extended.LeftTrigger.Value,
					RightTrigger = extended.RightTrigger.Value
				};
			}

			if (Controller.Gamepad is { } gamepad)
			{
				return new GameControllerState
				{
					South = gamepad.ButtonA.IsPressed,
					East = gamepad.ButtonB.IsPressed,
					West = gamepad.ButtonX.IsPressed,
					North = gamepad.ButtonY.IsPressed,
					LeftShoulder = gamepad.LeftShoulder.IsPressed,
					RightShoulder = gamepad.RightShoulder.IsPressed,
					DPadUp = gamepad.DPad.Up.IsPressed,
					DPadDown = gamepad.DPad.Down.IsPressed,
					DPadLeft = gamepad.DPad.Left.IsPressed,
					DPadRight = gamepad.DPad.Right.IsPressed
				};
			}

			if (Controller.MicroGamepad is { } micro)
			{
				return new GameControllerState
				{
					South = micro.ButtonA.IsPressed,
					West = micro.ButtonX.IsPressed,
					DPadUp = micro.Dpad.Up.IsPressed,
					DPadDown = micro.Dpad.Down.IsPressed,
					DPadLeft = micro.Dpad.Left.IsPressed,
					DPadRight = micro.Dpad.Right.IsPressed
				};
			}

			return new GameControllerState();
		}
	}
#pragma warning restore CA1422
}
#endif
