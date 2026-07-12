using CopperPad;

public sealed class GameControllerStateMappingTests
{
	[Fact]
	public void ExtendedStateMapsAllNormalizedControls()
	{
		var state = new GameControllerState
		{
			South = true,
			Start = true,
			LeftStickButton = true,
			DPadRight = true,
			LeftStickX = 0.75,
			LeftStickY = -0.5,
			LeftTrigger = 0.25
		};

		var snapshot = GameControllerSnapshotFactory.CreateSnapshot("apple:1", "Pad", true, true, state, DateTimeOffset.UtcNow);

		Assert.True(snapshot.South);
		Assert.True(snapshot.IsPressed(ControllerElement.Start));
		Assert.True(snapshot.IsPressed(ControllerElement.LeftStickButton));
		Assert.True(snapshot.IsPressed(ControllerElement.DPadRight));
		Assert.Equal(0.75, snapshot.GetAxis(ControllerElement.LeftStickX), precision: 3);
		Assert.Equal(-0.5, snapshot.GetAxis(ControllerElement.LeftStickY), precision: 3);
		Assert.Equal(0.25, snapshot.GetAxis(ControllerElement.LeftTrigger), precision: 3);
		Assert.Contains(ControllerProfileKind.ExtendedGamepad, snapshot.SupportedProfiles);
	}

	[Fact]
	public void StandardStateDoesNotAdvertiseExtendedProfile()
	{
		var snapshot = GameControllerSnapshotFactory.CreateSnapshot(
			"apple:2", "Remote", true, false, new GameControllerState { South = true }, DateTimeOffset.UtcNow);

		Assert.True(snapshot.South);
		Assert.Contains(ControllerProfileKind.StandardGamepad, snapshot.SupportedProfiles);
		Assert.DoesNotContain(ControllerProfileKind.ExtendedGamepad, snapshot.SupportedProfiles);
	}

	[Fact]
	public void DisconnectedStateContainsNoElements()
	{
		var snapshot = GameControllerSnapshotFactory.CreateSnapshot(
			"apple:3", "Pad", false, true, new GameControllerState { South = true }, DateTimeOffset.UtcNow);

		Assert.False(snapshot.IsConnected);
		Assert.Empty(snapshot.Elements);
	}

	[Fact]
	public void ProviderLifetimeIdsCanRepresentDuplicateVendorNames()
	{
		var first = GameControllerSnapshotFactory.CreateInfo("apple:first", "Same Vendor", true, true);
		var second = GameControllerSnapshotFactory.CreateInfo("apple:second", "Same Vendor", true, true);

		Assert.NotEqual(first.Id, second.Id);
		Assert.Equal(first.DisplayName, second.DisplayName);
	}
}
