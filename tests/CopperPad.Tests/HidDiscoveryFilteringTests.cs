using CopperPad;
using HidSharp;

public sealed class HidDiscoveryFilteringTests
{
	[Theory]
	[InlineData(1, 4, true)]
	[InlineData(1, 5, true)]
	[InlineData(1, 8, true)]
	[InlineData(1, 2, false)]
	[InlineData(1, 6, false)]
	[InlineData(12, 1, false)]
	[InlineData(0xff00, 4, false)]
	public void NativeUsageRecognizesOnlyGameControllerCollections(int page, int usage, bool expected)
		=> Assert.Equal(expected, new WindowsHidUsage((ushort)page, (ushort)usage).IsGameController);

	[Fact]
	public void NonControllerIsExcludedBeforeAnyDescriptorOrProductProbe()
	{
		var mouse = new ProbeDevice { FailIfInspected = true };
		var devices = HidSharpDeviceProvider.DescribeDevices([mouse], true, _ => new(1, 2));
		Assert.Empty(devices);
		Assert.Equal(0, mouse.Inspections);
	}

	[Theory]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(8)]
	[InlineData(0)] // Native query unavailable/disconnected: preserve existing discovery.
	public void ControllersAndUnknownUsageRetainDescriptorDiscovery(int usage)
	{
		var controller = new ProbeDevice();
		var devices = HidSharpDeviceProvider.DescribeDevices([controller], true,
			_ => usage == 0 ? null : new(1, (ushort)usage));
		Assert.Single(devices);
		Assert.Equal(1, controller.Inspections);
	}

	[Fact]
	public void UnfilteredDiagnosticsRetainAllDevicesWithoutTheNativeUsageProbe()
	{
		var device = new ProbeDevice();
		var devices = HidSharpDeviceProvider.DescribeDevices([device], false,
			_ => throw new InvalidOperationException("Unfiltered discovery must not query native usage."));
		Assert.Single(devices);
		Assert.Equal(1, device.Inspections);
	}

	[Fact]
	public void MissingNativeDeviceReturnsUnknownWithoutThrowing()
		=> Assert.Null(WindowsHidUsage.TryRead("\\\\?\\CopperPad-test-missing-hid-device"));

	private sealed class ProbeDevice : HidDevice
	{
		public bool FailIfInspected;
		public int Inspections;
		public override string DevicePath => "test-hid";
		public override int VendorID => 0x1234;
		public override int ProductID => 0x5678;
		public override int ReleaseNumberBcd => 0;
		public override string GetFileSystemName() => "test-hid";
		public override string GetFriendlyName() => "Test joystick";
		public override string GetProductName(GetStringFlags flags)
		{
			if (FailIfInspected) throw new InvalidOperationException("A non-controller was probed.");
			return "Test joystick";
		}
		public override byte[] GetRawReportDescriptor()
		{
			Inspections++;
			if (FailIfInspected) throw new NotImplementedException("Descriptor reconstruction must not run.");
			return [0x05, 0x01, 0x09, 0x04, 0xA1, 0x01, 0x09, 0x30, 0x15, 0, 0x26, 0xff, 0, 0x75, 8, 0x95, 1, 0x81, 2, 0xC0];
		}
		public override int GetMaxInputReportLength() => 2;
		public override int GetMaxOutputReportLength() => 0;
		public override int GetMaxFeatureReportLength() => 0;
		protected override DeviceStream OpenDeviceDirectly(OpenConfiguration openConfig) => throw new InvalidOperationException("Discovery must not open input streams.");
	}
}
