using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SkylandersCollection.Desktop;

internal static class HidPortal
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;

    public sealed class HidHandle : IDisposable
    {
        public required SafeFileHandle Handle { get; init; }
        public required string DevicePath { get; init; }
        public required HidCapabilities Capabilities { get; init; }

        public void Dispose()
        {
            if (!Handle.IsInvalid)
            {
                Handle.Dispose();
            }
        }
    }

    public sealed class HidCapabilities
    {
        public required int InputReportByteLength { get; init; }
        public required int OutputReportByteLength { get; init; }
        public required int FeatureReportByteLength { get; init; }
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
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    public static IReadOnlyList<string> FindDevicePaths(int vendorId, int productId)
    {
        return FindDevicePathsCore(vendorId, productId);
    }

    public static IReadOnlyList<string> FindDevicePathsByVendor(int vendorId)
    {
        return FindDevicePathsCore(vendorId, null);
    }

    private static IReadOnlyList<string> FindDevicePathsCore(int vendorId, int? productId)
    {
        HidD_GetHidGuid(out Guid hidGuid);
        List<string> results = [];
        IntPtr deviceInfoSet = SetupDiGetClassDevs(
            ref hidGuid,
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
                    ref hidGuid,
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

    public static HidHandle Open(string path)
    {
        SafeFileHandle handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile failed");
        }

        try
        {
            HiddAttributes attributes = new()
            {
                Size = Marshal.SizeOf<HiddAttributes>()
            };

            if (!HidD_GetAttributes(handle, ref attributes))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetAttributes failed");
            }

            HidCapabilities capabilities = GetCapabilities(handle);
            return new HidHandle
            {
                Handle = handle,
                DevicePath = path,
                Capabilities = capabilities
            };
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static byte[] Read(HidHandle handle)
    {
        int length = Math.Max(handle.Capabilities.InputReportByteLength, 33);
        byte[] buffer = new byte[length];

        if (!ReadFile(handle.Handle, buffer, (uint)buffer.Length, out uint transferred, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile failed");
        }

        if (transferred == buffer.Length)
        {
            return buffer;
        }

        byte[] trimmed = new byte[transferred];
        Array.Copy(buffer, trimmed, transferred);
        return trimmed;
    }

    public static void SetOutputReport(HidHandle handle, byte[] buffer)
    {
        // The Skylanders Portal of Power (including the Traptanium / Trap Team portal,
        // VID 0x1430 PID 0x0150) expects commands on the interrupt-OUT endpoint, which
        // means we have to use WriteFile against the HID handle. HidD_SetOutputReport
        // sends a SET_REPORT control transfer, which the portal firmware silently ignores
        // (connection looks fine, but figures never scan).
        if (WriteFile(handle.Handle, buffer, (uint)buffer.Length, out uint _, IntPtr.Zero))
        {
            return;
        }

        int writeError = Marshal.GetLastWin32Error();
        // Fall back to the older control-transfer path for portals that don't expose an
        // interrupt-OUT endpoint (rare, but harmless to try).
        if (HidD_SetOutputReport(handle.Handle, buffer, buffer.Length))
        {
            return;
        }

        throw new Win32Exception(writeError, "Portal output report failed (WriteFile + SetOutputReport)");
    }

    private static HidCapabilities GetCapabilities(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsedData))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_GetPreparsedData failed");
        }

        try
        {
            int status = HidP_GetCaps(preparsedData, out HidpCaps caps);
            if (status != 0x00110000)
            {
                throw new Win32Exception(status, "HidP_GetCaps failed");
            }

            return new HidCapabilities
            {
                InputReportByteLength = caps.InputReportByteLength,
                OutputReportByteLength = caps.OutputReportByteLength,
                FeatureReportByteLength = caps.FeatureReportByteLength
            };
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);
}
