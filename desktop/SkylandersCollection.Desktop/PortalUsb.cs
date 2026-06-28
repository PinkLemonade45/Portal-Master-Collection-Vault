using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SkylandersCollection.Desktop;

internal static class PortalUsb
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTransferTimeout = 0x03;

    private static readonly Guid UsbDeviceInterfaceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    public sealed class PortalHandle : IDisposable
    {
        public required SafeFileHandle DeviceHandle { get; init; }
        public required IntPtr WinUsbHandle { get; set; }
        public required byte InPipe { get; init; }
        public required byte OutPipe { get; init; }

        public void Dispose()
        {
            if (WinUsbHandle != IntPtr.Zero)
            {
                WinUsb_Free(WinUsbHandle);
                WinUsbHandle = IntPtr.Zero;
            }

            if (!DeviceHandle.IsInvalid)
            {
                DeviceHandle.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DeviceInterfaceDetailData
    {
        public int cbSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbInterfaceDescriptor
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WinUsbSetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    public static IReadOnlyList<string> FindDevicePaths(int vendorId, int productId, string? interfaceGuidText)
    {
        return FindDevicePathsCore(vendorId, productId, interfaceGuidText);
    }

    public static IReadOnlyList<string> FindDevicePathsByVendor(int vendorId, IEnumerable<string?> interfaceGuidTexts)
    {
        List<string> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        HashSet<string> seenInterfaceGuids = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? interfaceGuidText in interfaceGuidTexts.Prepend(null).Where(IsUsableGuidText))
        {
            string interfaceKey = interfaceGuidText ?? string.Empty;
            if (!seenInterfaceGuids.Add(interfaceKey))
            {
                continue;
            }

            foreach (string path in FindDevicePathsCore(vendorId, null, interfaceGuidText))
            {
                if (seen.Add(path))
                {
                    results.Add(path);
                }
            }
        }

        return results;

        static bool IsUsableGuidText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) || Guid.TryParse(value, out _);
        }
    }

    private static IReadOnlyList<string> FindDevicePathsCore(int vendorId, int? productId, string? interfaceGuidText)
    {
        var results = new List<string>();
        Guid interfaceGuid = string.IsNullOrWhiteSpace(interfaceGuidText)
            ? UsbDeviceInterfaceGuid
            : new Guid(interfaceGuidText);

        IntPtr deviceInfoSet = SetupDiGetClassDevs(
            ref interfaceGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet.ToInt64() == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");
        }

        try
        {
            for (int i = 0; ; i++)
            {
                DeviceInterfaceData interfaceData = new()
                {
                    cbSize = Marshal.SizeOf<DeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(
                    deviceInfoSet,
                    IntPtr.Zero,
                    ref interfaceGuid,
                    i,
                    ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed");
                }

                DeviceInterfaceDetailData detailData = new()
                {
                    cbSize = IntPtr.Size == 8 ? 8 : 6
                };

                if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    ref detailData,
                    Marshal.SizeOf<DeviceInterfaceDetailData>(),
                    out _,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed");
                }

                string lower = detailData.DevicePath.ToLowerInvariant();
                if (lower.Contains($"vid_{vendorId:x4}") &&
                    (productId is null || lower.Contains($"pid_{productId.Value:x4}")))
                {
                    results.Add(detailData.DevicePath);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    public static PortalHandle Open(string path)
    {
        SafeFileHandle deviceHandle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagOverlapped,
            IntPtr.Zero);

        if (deviceHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile failed");
        }

        if (!WinUsb_Initialize(deviceHandle, out IntPtr winUsbHandle))
        {
            deviceHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_Initialize failed");
        }

        if (!WinUsb_QueryInterfaceSettings(winUsbHandle, 0, out UsbInterfaceDescriptor descriptor))
        {
            WinUsb_Free(winUsbHandle);
            deviceHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_QueryInterfaceSettings failed");
        }

        byte inPipe = 0;
        byte outPipe = 0;

        for (byte pipeIndex = 0; pipeIndex < descriptor.bNumEndpoints; pipeIndex++)
        {
            if (!WinUsb_QueryPipe(winUsbHandle, 0, pipeIndex, out WinUsbPipeInformation pipe))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_QueryPipe failed");
            }

            bool isInPipe = (pipe.PipeId & 0x80) == 0x80;
            if (isInPipe && inPipe == 0)
            {
                inPipe = pipe.PipeId;
            }
            else if (!isInPipe && outPipe == 0)
            {
                outPipe = pipe.PipeId;
            }
        }

        if (inPipe == 0 || outPipe == 0)
        {
            WinUsb_Free(winUsbHandle);
            deviceHandle.Dispose();
            throw new InvalidOperationException("Expected at least one IN pipe and one OUT pipe.");
        }

        uint readTimeoutMs = 30;
        uint writeTimeoutMs = 250;
        WinUsb_SetPipePolicy(winUsbHandle, inPipe, PipeTransferTimeout, sizeof(uint), ref readTimeoutMs);
        WinUsb_SetPipePolicy(winUsbHandle, outPipe, PipeTransferTimeout, sizeof(uint), ref writeTimeoutMs);

        return new PortalHandle
        {
            DeviceHandle = deviceHandle,
            WinUsbHandle = winUsbHandle,
            InPipe = inPipe,
            OutPipe = outPipe
        };
    }

    public static void Write(PortalHandle handle, byte[] buffer)
    {
        if (!WinUsb_WritePipe(handle.WinUsbHandle, handle.OutPipe, buffer, (uint)buffer.Length, out _, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_WritePipe failed");
        }
    }

    /// <summary>
    /// Sends a HID-class SET_REPORT control transfer. Required for portals like the
    /// Trap Team / Wii U Skylanders Portal (VID 0x1430 PID 0x0150), which listens for
    /// commands on the control endpoint when run under WinUSB (Zadig) drivers instead
    /// of via the interrupt-OUT pipe.
    /// Returns true on success. Transient failures (timeouts, USB stalls) return false
    /// rather than throwing so a single bad command doesn't kill the scan loop.
    /// </summary>
    public static bool SetReportOutput(PortalHandle handle, byte[] reportData, byte reportId = 0x00)
    {
        WinUsbSetupPacket setup = new()
        {
            RequestType = 0x21,            // Host-to-Device, Class, Interface
            Request = 0x09,                // HID SET_REPORT
            Value = (ushort)((0x02 << 8) | reportId), // Output report
            Index = 0,                     // Interface 0
            Length = (ushort)reportData.Length
        };

        if (WinUsb_ControlTransfer(handle.WinUsbHandle, setup, reportData, (uint)reportData.Length, out _, IntPtr.Zero))
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        // Only escalate errors that genuinely mean the device is gone. Everything else —
        // including ERROR_GEN_FAILURE (31), which this portal raises frequently as a
        // transient control-transfer stall/NAK — is recoverable and resolves on the next
        // poll. Treating 31 as fatal caused constant disconnect/reconnect churn (slow
        // scans + repeated re-scans of the same figure).
        //   1167 = ERROR_DEVICE_NOT_CONNECTED
        //      2 = ERROR_FILE_NOT_FOUND (device path went away)
        //   1168 = ERROR_NOT_FOUND
        if (error is 1167 or 2 or 1168)
        {
            throw new Win32Exception(error, "WinUsb_ControlTransfer (SET_REPORT) failed: device disconnected");
        }

        return false;
    }

    public static byte[] Read(PortalHandle handle, int length)
    {
        byte[] buffer = new byte[length];

        if (!WinUsb_ReadPipe(handle.WinUsbHandle, handle.InPipe, buffer, (uint)buffer.Length, out uint transferred, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 121 or 1460)
            {
                return Array.Empty<byte>();
            }

            throw new Win32Exception(error, "WinUsb_ReadPipe failed");
        }

        if (transferred == buffer.Length)
        {
            return buffer;
        }

        byte[] trimmed = new byte[transferred];
        Array.Copy(buffer, trimmed, transferred);
        return trimmed;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        int memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        ref DeviceInterfaceDetailData deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        out UsbInterfaceDescriptor usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        byte pipeIndex,
        out WinUsbPipeInformation pipeInformation);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(
        IntPtr interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ControlTransfer(
        IntPtr interfaceHandle,
        WinUsbSetupPacket setupPacket,
        byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);
}
