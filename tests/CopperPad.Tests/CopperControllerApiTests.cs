using CopperPad;

public sealed class CopperControllerApiTests
{
	[Fact]
	public void Snapshot_ExposesCanonicalFaceButtonsAndXboxAliases()
	{
		var snapshot = new CopperControllerSnapshot(
			"pad",
			DateTimeOffset.UtcNow,
			true,
			"Pad",
			1,
			2,
			ControllerTransport.Usb,
			new Dictionary<ControllerElement, ControllerElementValue>
			{
				[ControllerElement.South] = ControllerElementValue.Button(true),
				[ControllerElement.East] = ControllerElementValue.Button(false),
				[ControllerElement.LeftStickX] = ControllerElementValue.Axis(0.5)
			},
			new HashSet<ControllerProfileKind> { ControllerProfileKind.StandardGamepad, ControllerProfileKind.ExtendedGamepad },
			ControllerMappingSource.ProviderNative,
			"Native",
			null);

		Assert.True(snapshot.South);
		Assert.True(snapshot.A);
		Assert.False(snapshot.B);
		Assert.Equal(0.5, snapshot.GetAxis(ControllerElement.LeftStickX), precision: 3);
		Assert.NotNull(snapshot.StandardGamepad);
		Assert.NotNull(snapshot.ExtendedGamepad);
	}

	[Fact]
	public void Host_PublishesProfileAndElementChanges()
	{
		using var provider = new FakeControllerProvider();
		using var host = new CopperControllerHost(provider);
		var info = new CopperControllerInfo(
			"pad",
			"Pad",
			1,
			2,
			ControllerTransport.Usb,
			true,
			new HashSet<ControllerProfileKind> { ControllerProfileKind.RawInput },
			ControllerMappingSource.None,
			null,
			null);
		provider.SetControllers(info);

		host.Start();
		var controller = Assert.Single(host.GetControllers());
		var profileChanges = 0;
		var elementChanges = 0;
		controller.ProfileChanged += (_, _) => profileChanges++;
		controller.ElementChanged += (_, args) =>
		{
			if (args.Element == ControllerElement.South)
			{
				elementChanges++;
			}
		};

		provider.Publish(new CopperControllerSnapshot(
			"pad",
			DateTimeOffset.UtcNow,
			true,
			"Pad",
			1,
			2,
			ControllerTransport.Usb,
			new Dictionary<ControllerElement, ControllerElementValue>
			{
				[ControllerElement.South] = ControllerElementValue.Button(true)
			},
			new HashSet<ControllerProfileKind> { ControllerProfileKind.StandardGamepad },
			ControllerMappingSource.UserProfile,
			"Override",
			null));

		Assert.Equal(1, profileChanges);
		Assert.Equal(1, elementChanges);
		Assert.True(controller.GetSnapshot().A);
	}

	[Fact]
	public void Host_DisconnectReleasesExistingElementsOnce()
	{
		using var provider = new FakeControllerProvider();
		using var host = new CopperControllerHost(provider);
		var info = Info();
		provider.SetControllers(info);
		host.Start();
		var controller = Assert.Single(host.GetControllers());
		var changes = new List<CopperElementChangedEventArgs>();
		controller.ElementChanged += (_, args) => changes.Add(args);
		provider.Publish(Snapshot(true, new Dictionary<ControllerElement, ControllerElementValue>
		{
			[ControllerElement.South] = ControllerElementValue.Button(true),
			[ControllerElement.LeftStickX] = ControllerElementValue.Axis(0.75)
		}));
		changes.Clear();

		provider.Publish(Snapshot(false, new Dictionary<ControllerElement, ControllerElementValue>()));

		Assert.Equal(2, changes.Count);
		Assert.Contains(changes, change => change.Element == ControllerElement.South && !change.CurrentValue.IsPressed);
		Assert.Contains(changes, change => change.Element == ControllerElement.LeftStickX && change.CurrentValue.Kind == ControllerElementValueKind.Axis && change.CurrentValue.Value == 0);
	}

	[Fact]
	public void Host_RemovingControllerPublishesDisconnectToHeldController()
	{
		using var provider = new FakeControllerProvider();
		using var host = new CopperControllerHost(provider);
		provider.SetControllers(Info());
		host.Start();
		var controller = Assert.Single(host.GetControllers());
		provider.Publish(Snapshot(true, new Dictionary<ControllerElement, ControllerElementValue>
		{
			[ControllerElement.South] = ControllerElementValue.Button(true)
		}));
		CopperElementChangedEventArgs? released = null;
		controller.ElementChanged += (_, args) => released = args;

		provider.SetControllers();
		provider.RaiseControllersChanged();

		Assert.NotNull(released);
		Assert.False(released.CurrentValue.IsPressed);
		Assert.False(controller.GetSnapshot().IsConnected);
	}

	[Fact]
	public void RuntimeStateDefensivelyCopiesMutableCollections()
	{
		var elements = new Dictionary<ControllerElement, ControllerElementValue>
		{
			[ControllerElement.South] = ControllerElementValue.Button(true)
		};
		var profiles = new HashSet<ControllerProfileKind> { ControllerProfileKind.StandardGamepad };
		var snapshot = Snapshot(true, elements, profiles);

		elements.Clear();
		profiles.Clear();

		Assert.True(snapshot.South);
		Assert.Contains(ControllerProfileKind.StandardGamepad, snapshot.SupportedProfiles);
	}

	private static CopperControllerInfo Info()
		=> new("pad", "Pad", 1, 2, ControllerTransport.Usb, true, [ControllerProfileKind.StandardGamepad], ControllerMappingSource.UserProfile, "test", null);

	private static CopperControllerSnapshot Snapshot(
		bool connected,
		IEnumerable<KeyValuePair<ControllerElement, ControllerElementValue>> elements,
		IEnumerable<ControllerProfileKind>? profiles = null)
		=> new("pad", DateTimeOffset.UtcNow, connected, "Pad", 1, 2, ControllerTransport.Usb, elements,
			profiles ?? [ControllerProfileKind.StandardGamepad], ControllerMappingSource.UserProfile, "test", null);

	private sealed class FakeControllerProvider : IControllerProvider
	{
		private IReadOnlyList<CopperControllerInfo> _controllers = Array.Empty<CopperControllerInfo>();

		public event EventHandler<CopperControllersChangedEventArgs>? ControllersChanged;
		public event EventHandler<CopperControllerSnapshotChangedEventArgs>? SnapshotChanged;

		public void Start()
			=> ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(_controllers));

		public void Stop()
		{
		}

		public IReadOnlyList<CopperControllerInfo> GetControllers()
			=> _controllers;

		public bool TryGetSnapshot(string controllerId, out CopperControllerSnapshot snapshot)
		{
			snapshot = null!;
			return false;
		}

		public void SetControllers(params CopperControllerInfo[] controllers)
			=> _controllers = controllers;

		public void Publish(CopperControllerSnapshot snapshot)
			=> SnapshotChanged?.Invoke(this, new CopperControllerSnapshotChangedEventArgs(snapshot));

		public void RaiseControllersChanged()
			=> ControllersChanged?.Invoke(this, new CopperControllersChangedEventArgs(_controllers));

		public void Dispose()
		{
		}
	}
}
