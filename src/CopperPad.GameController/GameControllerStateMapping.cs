using System.Collections.Immutable;

namespace CopperPad;

internal sealed record GameControllerState
{
	public bool South { get; init; }
	public bool East { get; init; }
	public bool West { get; init; }
	public bool North { get; init; }
	public bool LeftShoulder { get; init; }
	public bool RightShoulder { get; init; }
	public bool Select { get; init; }
	public bool Start { get; init; }
	public bool Menu { get; init; }
	public bool LeftStickButton { get; init; }
	public bool RightStickButton { get; init; }
	public bool DPadUp { get; init; }
	public bool DPadDown { get; init; }
	public bool DPadLeft { get; init; }
	public bool DPadRight { get; init; }
	public double LeftStickX { get; init; }
	public double LeftStickY { get; init; }
	public double RightStickX { get; init; }
	public double RightStickY { get; init; }
	public double LeftTrigger { get; init; }
	public double RightTrigger { get; init; }
}

internal static class GameControllerSnapshotFactory
{
	private static readonly ImmutableHashSet<ControllerProfileKind> StandardProfiles =
	[
		ControllerProfileKind.StandardGamepad
	];

	private static readonly ImmutableHashSet<ControllerProfileKind> ExtendedProfiles =
	[
		ControllerProfileKind.StandardGamepad,
		ControllerProfileKind.ExtendedGamepad
	];

	public static CopperControllerInfo CreateInfo(string id, string name, bool connected, bool extended)
		=> new(
			id,
			name,
			0,
			0,
			ControllerTransport.Unknown,
			connected,
			extended ? ExtendedProfiles : StandardProfiles,
			ControllerMappingSource.ProviderNative,
			"Apple GameController",
			null);

	public static CopperControllerSnapshot CreateSnapshot(
		string id,
		string name,
		bool connected,
		bool extended,
		GameControllerState state,
		DateTimeOffset timestamp)
	{
		var elements = connected
			? new Dictionary<ControllerElement, ControllerElementValue>
			{
				[ControllerElement.South] = ControllerElementValue.Button(state.South),
				[ControllerElement.East] = ControllerElementValue.Button(state.East),
				[ControllerElement.West] = ControllerElementValue.Button(state.West),
				[ControllerElement.North] = ControllerElementValue.Button(state.North),
				[ControllerElement.LeftShoulder] = ControllerElementValue.Button(state.LeftShoulder),
				[ControllerElement.RightShoulder] = ControllerElementValue.Button(state.RightShoulder),
				[ControllerElement.Select] = ControllerElementValue.Button(state.Select),
				[ControllerElement.Start] = ControllerElementValue.Button(state.Start),
				[ControllerElement.Menu] = ControllerElementValue.Button(state.Menu),
				[ControllerElement.LeftStickButton] = ControllerElementValue.Button(state.LeftStickButton),
				[ControllerElement.RightStickButton] = ControllerElementValue.Button(state.RightStickButton),
				[ControllerElement.DPadUp] = ControllerElementValue.Button(state.DPadUp),
				[ControllerElement.DPadDown] = ControllerElementValue.Button(state.DPadDown),
				[ControllerElement.DPadLeft] = ControllerElementValue.Button(state.DPadLeft),
				[ControllerElement.DPadRight] = ControllerElementValue.Button(state.DPadRight),
				[ControllerElement.LeftStickX] = ControllerElementValue.Axis(state.LeftStickX),
				[ControllerElement.LeftStickY] = ControllerElementValue.Axis(state.LeftStickY),
				[ControllerElement.RightStickX] = ControllerElementValue.Axis(state.RightStickX),
				[ControllerElement.RightStickY] = ControllerElementValue.Axis(state.RightStickY),
				[ControllerElement.LeftTrigger] = ControllerElementValue.Trigger(state.LeftTrigger),
				[ControllerElement.RightTrigger] = ControllerElementValue.Trigger(state.RightTrigger)
			}
			: new Dictionary<ControllerElement, ControllerElementValue>();

		return new CopperControllerSnapshot(
			id,
			timestamp,
			connected,
			name,
			0,
			0,
			ControllerTransport.Unknown,
			elements,
			extended ? ExtendedProfiles : StandardProfiles,
			ControllerMappingSource.ProviderNative,
			"Apple GameController",
			null);
	}
}
