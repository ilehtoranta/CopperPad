using CopperPad;

public sealed class MapperSafetyTests
{
	[Theory]
	[InlineData(2048, 0)]
	[InlineData(0, 1)]
	[InlineData(4095, -1)]
	public void SwitchPackedYDecodesBothSticks(int rawY, double expected)
	{
		var device = ControllerMapperTests.Device(0x057e, 0x2009, "Switch Pro", maxInputLength: 14);
		var report = new byte[14];
		report[0] = 0x30;
		// X occupies the low nibble of the shared byte; Y occupies its high nibble.
		Pack(report, 6, 1234, rawY);
		Pack(report, 9, 3456, rawY);
		var snapshot = new SwitchProControllerMapper().Map(new(device, report, report.Length, DateTimeOffset.UtcNow));
		Assert.Equal(expected, snapshot.GetAxis(ControllerElement.LeftStickY), 6);
		Assert.Equal(expected, snapshot.GetAxis(ControllerElement.RightStickY), 6);
		Assert.Equal(InputNormalization.NormalizeAxis(1234, 0, 4095), snapshot.GetAxis(ControllerElement.LeftStickX), 6);
		Assert.Equal(InputNormalization.NormalizeAxis(3456, 0, 4095), snapshot.GetAxis(ControllerElement.RightStickX), 6);
	}

	private static void Pack(byte[] report, int offset, int x, int y)
	{
		report[offset] = (byte)x;
		report[offset + 1] = (byte)((x >> 8) | ((y & 15) << 4));
		report[offset + 2] = (byte)(y >> 4);
	}

	[Theory]
	[InlineData(0x045e, 0x07f8, "Microsoft Keyboard")]
	[InlineData(0x054c, 0x0001, "Sony Keyboard")]
	[InlineData(0x057e, 0x0001, "Nintendo Adapter")]
	[InlineData(0x1234, 0x5678, "DualShock DualSense Xbox Switch Pro Controller")]
	[InlineData(0x054c, 0x0ce6, "DualSense")]
	public void ManufacturerAndNameDoNotSelectKnownLayouts(int vendor, int product, string name)
	{
		var device = ControllerMapperTests.Device(vendor, product, name, maxInputLength: 64);
		Assert.False(KnownControllerMapper.TryCreate(device, out _));
		Assert.False(ControllerMapperFactory.Create(device, ControllerProfileSet.Empty) is PlayStationControllerMapper or XboxControllerMapper or SwitchProControllerMapper);
	}

	[Theory]
	[InlineData(0x054c, 0x05c4, 10)]
	[InlineData(0x054c, 0x09cc, 10)]
	[InlineData(0x057e, 0x2009, 12)]
	[InlineData(0x045e, 0x02ea, 14)]
	public void SupportedProductsRequireControllerLayouts(int vendor, int product, int length)
	{
		var device = ControllerMapperTests.Device(vendor, product, "HID", maxInputLength: length);
		Assert.True(KnownControllerMapper.TryCreate(device, out _));
		Assert.False(KnownControllerMapper.TryCreate(device with { MaxInputReportLength = length - 1 }, out _));
		Assert.False(KnownControllerMapper.TryCreate(device with { ReportDescriptor = [1], IsGameControllerUsage = false }, out _));
		Assert.True(KnownControllerMapper.TryCreate(device with { ReportDescriptor = [1], IsGameControllerUsage = true }, out _));
	}

	[Fact]
	public void ExplicitProfileOverridesIdentificationAndUsageFiltering()
	{
		var device = ControllerMapperTests.Device(0x045e, 0x07f8, "Microsoft Keyboard");
		var profile = new ControllerProfile { Name = "Explicit", VendorId = device.VendorId, ProductId = device.ProductId };
		var profiles = new ControllerProfileSet { Profiles = [profile] };
		Assert.True(ControllerMapperFactory.IsCandidate(device, profiles, requireGameControllerUsage: true));
		Assert.IsType<ProfileControllerMapper>(ControllerMapperFactory.Create(device, profiles));
	}

	[Fact]
	public void DiagnosticCapabilitiesAreRawOnlyInSnapshotsAndMetadata()
	{
		var device = ControllerMapperTests.Device(0x9999, 0x8888, "Unknown HID controller");
		var mapper = ControllerMapperFactory.Create(device, ControllerProfileSet.Empty);
		var snapshot = mapper.Map(new(device, [0], 1, DateTimeOffset.UtcNow));
		var disconnected = CopperControllerSnapshotBuilder.Disconnected(device, DateTimeOffset.UtcNow, mapper.MappingInfo, "Read failed");
		var info = CopperControllerSnapshotBuilder.ToInfo(device, false, mapper.MappingInfo, null);
		Assert.Equal(ControllerProfileKind.RawInput, Assert.Single(info.SupportedProfiles));
		foreach (var value in new[] { snapshot, disconnected })
		{
			Assert.Equal(ControllerProfileKind.RawInput, Assert.Single(value.SupportedProfiles));
			Assert.Empty(value.Elements);
			Assert.NotNull(value.RawInput);
			Assert.Null(value.StandardGamepad);
			Assert.Null(value.ExtendedGamepad);
			Assert.Equal(ControllerMappingSource.None, value.MappingSource);
		}
		var mapped = CopperControllerSnapshotBuilder.ToInfo(device, true, new("User profile", "Explicit"), null);
		Assert.Contains(ControllerProfileKind.ExtendedGamepad, mapped.SupportedProfiles);
	}

	[Theory]
	[InlineData(ControllerElement.LeftStickX, 0, 1)]
	[InlineData(ControllerElement.LeftStickY, 1, 2)]
	[InlineData(ControllerElement.RightTrigger, 0, 1)]
	[InlineData(ControllerElement.South, 0, 1)]
	[InlineData(ControllerElement.LeftStickX, int.MaxValue, 2)]
	public void IncompleteSignedSourcesStayNeutralAndDiagnose(ControllerElement target, int offset, int length)
	{
		var device = ControllerMapperTests.Device(1, 2, "Pad");
		var source = new ControllerBindingSource { Kind = ControllerBindingSourceKind.ReportInt16LittleEndian, Offset = offset, Invert = true };
		var profile = new ControllerProfile { Name = "Signed", Bindings = [new() { Target = target, Source = source, Axis = new() { Minimum = short.MinValue, Maximum = short.MaxValue, Center = 0, Deadzone = 0, Invert = true } }] };
		var mapper = new ProfileControllerMapper(profile);
		var snapshot = mapper.Map(new(device, [255, 127], length, DateTimeOffset.UtcNow));
		Assert.Equal(0, snapshot.GetAxis(target));
		Assert.False(snapshot.IsPressed(target));
		Assert.Contains("incomplete report source", snapshot.Diagnostic);
		Assert.Throws<ArgumentException>(() => ProfileControllerMapper.ReadAxisSource(source, [255, 127], length));
		Assert.False(ProfileControllerMapper.ReadButtonSource(source, [255, 127], length));
	}

	[Theory]
	[InlineData(short.MinValue, -1)]
	[InlineData(0, 0)]
	[InlineData(short.MaxValue, 1)]
	public void CompleteSignedSourcesRetainFullRange(short raw, double expected)
	{
		var device = ControllerMapperTests.Device(1, 2, "Pad");
		var profile = new ControllerProfile { Name = "Signed", Bindings = [new() { Target = ControllerElement.LeftStickX, Source = new() { Kind = ControllerBindingSourceKind.ReportInt16LittleEndian }, Axis = new() { Minimum = short.MinValue, Maximum = short.MaxValue, Center = 0, Deadzone = 0 } }] };
		var snapshot = new ProfileControllerMapper(profile).Map(new(device, BitConverter.GetBytes(raw), 2, DateTimeOffset.UtcNow));
		Assert.Equal(expected, snapshot.GetAxis(ControllerElement.LeftStickX));
		Assert.Null(snapshot.Diagnostic);
	}
}
