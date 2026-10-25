/*
 * Copyright (C) 2026 Ilkka Lehtoranta
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopperPad;

internal readonly record struct WindowsHidUsage(ushort Page, ushort Usage)
{
	internal bool IsGameController => Page == 1 && Usage is 4 or 5 or 8;

	internal static WindowsHidUsage? TryRead(string devicePath)
	{
		if (!OperatingSystem.IsWindows()) return null;
		// Metadata only: do not request read/write access to keyboards or mice.
		using var handle = CreateFile(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
		if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out var data)) return null;
		try
		{
			return HidP_GetCaps(data, out var caps) == 0x00110000
				? new(caps.UsagePage, caps.Usage) : null;
		}
		finally { HidD_FreePreparsedData(data); }
	}

	// HIDP_CAPS is 32 USHORTs. Only the top-level usage fields are needed here.
	// https://learn.microsoft.com/windows-hardware/drivers/ddi/hidpi/ns-hidpi-_hidp_caps
	[StructLayout(LayoutKind.Explicit, Size = 64)]
	private struct Capabilities
	{
		[FieldOffset(0)] public ushort Usage;
		[FieldOffset(2)] public ushort UsagePage;
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true)]
	private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
		uint disposition, uint flags, IntPtr template);

	[DllImport("hid.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);

	[DllImport("hid.dll", ExactSpelling = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	private static extern bool HidD_FreePreparsedData(IntPtr data);

	[DllImport("hid.dll", ExactSpelling = true)]
	private static extern int HidP_GetCaps(IntPtr data, out Capabilities capabilities);
}
