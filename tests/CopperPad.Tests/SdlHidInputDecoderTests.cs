using CopperPad;

public sealed class SdlHidInputDecoderTests
{
    private static HidDeviceDescriptor Device(byte[] descriptor, bool ids = true) =>
        ControllerMapperTests.Device(0x1234, 0x5678, "Descriptor Gamepad", reportsUseId: ids) with { ReportDescriptor = descriptor };
    private static RawControllerInput Input(HidDeviceDescriptor device, byte[] report, int? length = null) => new(device, report, length ?? report.Length, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(-32768)]
    [InlineData(0)]
    [InlineData(32767)]
    public void DecodesSignedAxesAndButtonsFromUnnumberedReports(short raw)
    {
        byte[] descriptor = [0x05,0x01,0x09,0x05,0xa1,0x01,
            0x09,0x30,0x16,0x00,0x80,0x26,0xff,0x7f,0x75,0x10,0x95,0x01,0x81,0x02,
            0x05,0x09,0x09,0x01,0x15,0x00,0x25,0x01,0x75,0x01,0x95,0x01,0x81,0x02,
            0x75,0x07,0x95,0x01,0x81,0x03,0xc0];
        var device = Device(descriptor, ids: false);
        var decoded = new SdlHidInputDecoder(device).Decode(Input(device, [0,unchecked((byte)raw),unchecked((byte)(raw >> 8)),1]));
        Assert.Null(decoded.Diagnostic);
        Assert.Equal(raw, decoded.GetAxis(0)!.Value.Raw);
        Assert.Equal(short.MinValue, decoded.GetAxis(0)!.Value.Minimum);
        Assert.Equal(short.MaxValue, decoded.GetAxis(0)!.Value.Maximum);
        Assert.True(decoded.GetButton(0));
    }

    [Fact]
    public void NullHatsAndUnreadAxesKeepTheirIndexesAcrossReportIds()
    {
        byte[] descriptor = [0x05,0x01,0x09,0x05,0xa1,0x01,
            0x85,0x01,0x09,0x39,0x15,0x01,0x25,0x08,0x75,0x04,0x95,0x01,0x81,0x42,0x75,0x04,0x95,0x01,0x81,0x03,
            0x85,0x02,0x09,0x39,0x75,0x04,0x95,0x01,0x81,0x42,0x75,0x04,0x95,0x01,0x81,0x03,
            0x85,0x03,0x09,0x30,0x15,0x00,0x26,0xff,0x00,0x75,0x08,0x95,0x01,0x81,0x02,
            0x85,0x04,0x09,0x31,0x75,0x08,0x95,0x01,0x81,0x02,0xc0];
        var device = Device(descriptor); var decoder = new SdlHidInputDecoder(device);
        var north = decoder.Decode(Input(device, [2,1]));
        Assert.Equal(2, north.Hats.Count); Assert.Equal(0, north.GetHat(0)); Assert.Equal(1, north.GetHat(1));
        Assert.Equal(2, north.Axes.Count); Assert.Null(north.GetAxis(0)); Assert.Null(north.GetAxis(1));
        var y = decoder.Decode(Input(device, [4,255]));
        Assert.Null(y.GetAxis(0)); Assert.Equal(255, y.GetAxis(1)!.Value.Raw);
        var x = decoder.Decode(Input(device, [3,128]));
        Assert.Equal(128, x.GetAxis(0)!.Value.Raw); Assert.Equal(255, x.GetAxis(1)!.Value.Raw);
        var released = decoder.Decode(Input(device, [2,0]));
        Assert.Equal(0, released.GetHat(1)); Assert.Equal(2, released.Hats.Count);
        Assert.Null(released.Diagnostic);
    }

    [Fact]
    public void UnknownAndTruncatedReportsDiagnoseFailureWithoutGuessingInput()
    {
        byte[] descriptor = [0x05,0x01,0x09,0x05,0xa1,0x01,0x85,0x01,0x09,0x30,0x15,0x00,0x26,0xff,0x00,0x75,0x08,0x95,0x01,0x81,0x02,0xc0];
        var device = Device(descriptor); var decoder = new SdlHidInputDecoder(device);
        Assert.Equal(255, decoder.Decode(Input(device, [1,255])).GetAxis(0)!.Value.Raw);
        var unknown = decoder.Decode(Input(device, [2,0]));
        Assert.Contains("report ID", unknown.Diagnostic); Assert.Equal(255, unknown.GetAxis(0)!.Value.Raw);
        var truncated = decoder.Decode(Input(device, [1,0], length: 1));
        Assert.Contains("too short", truncated.Diagnostic); Assert.Equal(255, truncated.GetAxis(0)!.Value.Raw);
        Assert.Null(decoder.Decode(Input(device, [1,0])).Diagnostic);
    }

    [Fact]
    public void MalformedDescriptorFallbackIsDiagnosed()
    {
        // End collection without a matching start is not a valid HID descriptor.
        var device = Device([0xc0]);
        var result = new SdlHidInputDecoder(device).Decode(Input(device, [1,255,128]));
        Assert.Contains("raw fallback", result.Diagnostic);
        Assert.Contains("no input reports", result.Diagnostic);
        Assert.Equal(255, result.GetAxis(0)!.Value.Raw);
    }

    [Fact]
    public void MissingDescriptorFallbackIsDiagnosedAndSkipsNumberedReportId()
    {
        var device = Device([]); var result = new SdlHidInputDecoder(device).Decode(Input(device, [1,255,128]));
        Assert.Contains("raw fallback", result.Diagnostic); Assert.Equal(255, result.GetAxis(0)!.Value.Raw);
    }
}
