/*
 * Copyright (C) 2026 Ilkka Lehtoranta
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CopperPad;

/// <summary>
/// Root document for controller profile JSON.
/// </summary>
/// <remarks>Profiles are evaluated in list order; the first matching profile wins.</remarks>
public sealed record ControllerProfileSet
{
	/// <summary>Gets an empty profile set.</summary>
	public static ControllerProfileSet Empty { get; } = new();

	/// <summary>Gets the profile document schema version.</summary>
	public int SchemaVersion { get; init; } = 2;
	/// <summary>Gets the controller profiles in match order.</summary>
	public IReadOnlyList<ControllerProfile> Profiles { get; init; } = Array.Empty<ControllerProfile>();

	/// <summary>
	/// Finds the first profile that matches controller metadata.
	/// </summary>
	/// <param name="info">Controller metadata to match.</param>
	/// <returns>The matching profile, or <see langword="null"/> when none match.</returns>
	public ControllerProfile? FindMatch(CopperControllerInfo info)
	{
		foreach (var profile in Profiles)
		{
			if (profile.Matches(info))
			{
				return profile;
			}
		}

		return null;
	}
}

/// <summary>
/// Describes a user-defined mapping for one controller or controller family.
/// </summary>
public sealed record ControllerProfile
{
	/// <summary>Gets the display name of the profile.</summary>
	public string Name { get; init; } = string.Empty;
	/// <summary>Gets optional notes for humans editing the profile.</summary>
	public string? Notes { get; init; }
	/// <summary>Gets the matched USB/HID vendor identifier, or <see langword="null"/> to match any vendor.</summary>
	public int? VendorId { get; init; }
	/// <summary>Gets the matched USB/HID product identifier, or <see langword="null"/> to match any product.</summary>
	public int? ProductId { get; init; }
	/// <summary>Gets a case-insensitive product-name substring required for matching.</summary>
	public string? ProductNameContains { get; init; }
	/// <summary>Gets an exact provider device identifier required for matching duplicate devices.</summary>
	public string? DeviceId { get; init; }
	/// <summary>Gets when the profile was created, when known.</summary>
	public DateTimeOffset? CreatedAt { get; init; }
	/// <summary>Gets when the profile was last updated, when known.</summary>
	public DateTimeOffset? UpdatedAt { get; init; }
	/// <summary>Gets the bindings from physical report sources to normalized elements.</summary>
	public IReadOnlyList<ControllerBinding> Bindings { get; init; } = Array.Empty<ControllerBinding>();

	/// <summary>
	/// Gets whether this profile matches controller metadata.
	/// </summary>
	/// <param name="info">Controller metadata to test.</param>
	/// <returns><see langword="true"/> when the profile matches.</returns>
	public bool Matches(CopperControllerInfo info)
	{
		if (VendorId.HasValue && VendorId.Value != info.VendorId)
		{
			return false;
		}

		if (ProductId.HasValue && ProductId.Value != info.ProductId)
		{
			return false;
		}

		if (!string.IsNullOrWhiteSpace(DeviceId) && !string.Equals(DeviceId, info.Id, StringComparison.Ordinal))
		{
			return false;
		}

		return string.IsNullOrWhiteSpace(ProductNameContains) ||
			info.DisplayName.Contains(ProductNameContains, StringComparison.OrdinalIgnoreCase);
	}
}

/// <summary>
/// Maps one physical input source to one normalized controller element.
/// </summary>
public sealed record ControllerBinding
{
	/// <summary>Gets the normalized element to update.</summary>
	public ControllerElement Target { get; init; }
	/// <summary>Gets the physical report source.</summary>
	public ControllerBindingSource Source { get; init; } = new();
	/// <summary>Gets optional axis calibration for axis or trigger targets.</summary>
	public AxisCalibration? Axis { get; init; }
}

/// <summary>
/// Identifies how a binding reads from an input report.
/// </summary>
public enum ControllerBindingSourceKind
{
	/// <summary>A single bit in one report byte.</summary>
	ReportBit,
	/// <summary>A whole report byte.</summary>
	ReportByte,
	/// <summary>A little-endian signed 16-bit value.</summary>
	ReportInt16LittleEndian,
	/// <summary>A HID hat-switch value.</summary>
	Hat
}

/// <summary>
/// Describes where one binding reads its physical value from a raw report.
/// </summary>
public sealed record ControllerBindingSource
{
	/// <summary>Gets the source encoding.</summary>
	public ControllerBindingSourceKind Kind { get; init; }
	/// <summary>Gets the zero-based byte offset in the input report.</summary>
	public int Offset { get; init; }
	/// <summary>Gets the bit index for <see cref="ControllerBindingSourceKind.ReportBit"/> sources.</summary>
	public int Bit { get; init; }
	/// <summary>Gets the expected hat value for <see cref="ControllerBindingSourceKind.Hat"/> sources.</summary>
	public int? HatValue { get; init; }
	/// <summary>Gets whether the source is logically active when the physical value is cleared.</summary>
	public bool Invert { get; init; }
}

/// <summary>
/// Describes app-level calibration for a physical axis.
/// </summary>
public sealed record AxisCalibration
{
	/// <summary>Gets the raw minimum value.</summary>
	public int Minimum { get; init; } = 0;
	/// <summary>Gets the raw maximum value.</summary>
	public int Maximum { get; init; } = 255;
	/// <summary>Gets the raw center value for bidirectional axes.</summary>
	public int? Center { get; init; }
	/// <summary>Gets whether the normalized value should be inverted.</summary>
	public bool Invert { get; init; }
	/// <summary>Gets the normalized deadzone in the 0..0.95 range.</summary>
	public double Deadzone { get; init; } = 0.1;
	/// <summary>Gets the normalized saturation multiplier in the 0.1..1 range.</summary>
	public double Saturation { get; init; } = 1.0;
}

/// <summary>
/// Loads and saves controller profile sets.
/// </summary>
public interface IControllerProfileStore
{
	/// <summary>
	/// Loads a profile set.
	/// </summary>
	/// <param name="cancellationToken">Token used to cancel the load.</param>
	/// <returns>The loaded profile set.</returns>
	ValueTask<ControllerProfileSet> LoadAsync(CancellationToken cancellationToken = default);
	/// <summary>
	/// Saves a profile set.
	/// </summary>
	/// <param name="profiles">Profiles to save.</param>
	/// <param name="cancellationToken">Token used to cancel the save.</param>
	/// <returns>A task that completes when the save is finished.</returns>
	ValueTask SaveAsync(ControllerProfileSet profiles, CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes and deserializes CopperPad controller profile JSON.
/// </summary>
public static class JsonControllerProfileSerializer
{
	private static readonly JsonSerializerOptions Options = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	/// <summary>
	/// Serializes a profile set as JSON.
	/// </summary>
	/// <param name="profiles">Profiles to serialize.</param>
	/// <returns>Formatted JSON using the current schema version.</returns>
	public static string Serialize(ControllerProfileSet profiles)
		=> JsonSerializer.Serialize(ValidateAndUpgrade(profiles), Options);

	/// <summary>
	/// Deserializes a profile set from JSON.
	/// </summary>
	/// <param name="json">Profile JSON.</param>
	/// <returns>The deserialized profile set upgraded to the current schema version.</returns>
	public static ControllerProfileSet Deserialize(string json)
		=> ValidateAndUpgrade(JsonSerializer.Deserialize<ControllerProfileSet>(json, Options) ?? ControllerProfileSet.Empty);

	/// <summary>
	/// Loads a profile set from a stream.
	/// </summary>
	/// <param name="stream">Stream containing profile JSON.</param>
	/// <param name="cancellationToken">Token used to cancel the load.</param>
	/// <returns>The loaded profile set upgraded to the current schema version.</returns>
	public static async ValueTask<ControllerProfileSet> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
		=> ValidateAndUpgrade(await JsonSerializer.DeserializeAsync<ControllerProfileSet>(stream, Options, cancellationToken).ConfigureAwait(false) ??
			ControllerProfileSet.Empty);

	/// <summary>
	/// Saves a profile set to a stream as JSON.
	/// </summary>
	/// <param name="stream">Destination stream.</param>
	/// <param name="profiles">Profiles to save.</param>
	/// <param name="cancellationToken">Token used to cancel the save.</param>
	/// <returns>A task that completes when serialization is finished.</returns>
	public static async ValueTask SaveAsync(Stream stream, ControllerProfileSet profiles, CancellationToken cancellationToken = default)
		=> await JsonSerializer.SerializeAsync(stream, ValidateAndUpgrade(profiles), Options, cancellationToken).ConfigureAwait(false);

	private static ControllerProfileSet ValidateAndUpgrade(ControllerProfileSet profiles)
	{
		if (profiles.SchemaVersion is < 1 or > 2)
		{
			throw new JsonException($"Unsupported controller profile schema version {profiles.SchemaVersion}.");
		}

		if (profiles.Profiles == null)
		{
			throw new JsonException("The profiles collection cannot be null.");
		}

		for (var profileIndex = 0; profileIndex < profiles.Profiles.Count; profileIndex++)
		{
			var profile = profiles.Profiles[profileIndex];
			if (profile == null)
			{
				throw new JsonException($"Profile {profileIndex} cannot be null.");
			}

			ValidateProfile(profile, profileIndex);
		}

		return profiles.SchemaVersion == 2 ? profiles : profiles with { SchemaVersion = 2 };
	}

	private static void ValidateProfile(ControllerProfile profile, int profileIndex)
	{
		if (string.IsNullOrWhiteSpace(profile.Name))
		{
			throw new JsonException($"Profile {profileIndex} must have a name.");
		}

		if (profile.Bindings == null)
		{
			throw new JsonException($"Profile '{profile.Name}' bindings cannot be null.");
		}

		var duplicate = profile.Bindings.GroupBy(binding => binding?.Target).FirstOrDefault(group => group.Count() > 1);
		if (duplicate != null)
		{
			throw new JsonException($"Profile '{profile.Name}' has duplicate bindings for {duplicate.Key}.");
		}

		for (var bindingIndex = 0; bindingIndex < profile.Bindings.Count; bindingIndex++)
		{
			var binding = profile.Bindings[bindingIndex];
			if (binding == null || binding.Source == null)
			{
				throw new JsonException($"Profile '{profile.Name}' binding {bindingIndex} is incomplete.");
			}

			ValidateBinding(profile.Name, binding);
		}
	}

	private static void ValidateBinding(string profileName, ControllerBinding binding)
	{
		var source = binding.Source;
		if (!Enum.IsDefined(source.Kind) || !Enum.IsDefined(binding.Target))
		{
			throw new JsonException($"Profile '{profileName}' contains an unknown binding value.");
		}

		if (source.Offset < 0)
		{
			throw new JsonException($"Profile '{profileName}' binding {binding.Target} has a negative offset.");
		}

		if (source.Kind == ControllerBindingSourceKind.ReportBit && source.Bit is < 0 or > 7)
		{
			throw new JsonException($"Profile '{profileName}' binding {binding.Target} has an invalid bit index.");
		}

		if (source.Kind == ControllerBindingSourceKind.Hat && source.HatValue is not (>= 0 and <= 15))
		{
			throw new JsonException($"Profile '{profileName}' binding {binding.Target} has an invalid hat value.");
		}

		if (binding.Axis is { } axis)
		{
			if (axis.Maximum <= axis.Minimum || axis.Center is { } center && (center < axis.Minimum || center > axis.Maximum))
			{
				throw new JsonException($"Profile '{profileName}' binding {binding.Target} has an invalid calibration range.");
			}

			if (!double.IsFinite(axis.Deadzone) || axis.Deadzone is < 0 or > 0.95 ||
				!double.IsFinite(axis.Saturation) || axis.Saturation is < 0.01 or > 1)
			{
				throw new JsonException($"Profile '{profileName}' binding {binding.Target} has invalid calibration options.");
			}
		}
	}
}
