namespace SkylandersCollection.Desktop;

internal interface IPortalConnection : IDisposable
{
    string Description { get; }
    int? ProductId { get; }
    void WriteCommand(byte command, byte[]? payload = null);
    byte[] Read();
}

internal sealed class WinUsbPortalConnection : IPortalConnection
{
    private readonly PortalUsb.PortalHandle _handle;
    private readonly PortalProtocol _protocol;

    public WinUsbPortalConnection(PortalUsb.PortalHandle handle, string description, PortalProtocol protocol, int? productId)
    {
        _handle = handle;
        Description = description;
        _protocol = protocol;
        ProductId = productId;
    }

    public string Description { get; }
    public int? ProductId { get; }

    public void WriteCommand(byte command, byte[]? payload = null)
    {
        if (_protocol == PortalProtocol.HidStyle)
        {
            // Trap Team / Wii U portal (VID 0x1430 PID 0x0150). Under native HID, commands
            // travel via SET_REPORT control transfers — the interrupt-OUT pipe is just for
            // streaming responses back. When the portal is rebound to WinUSB (Zadig for
            // Cemu), the firmware still only listens on the control endpoint, so we send
            // commands as USB control SET_REPORT (output report ID 0, 32-byte payload).
            byte[] report = new byte[32];
            report[0] = command;
            if (payload is not null)
            {
                Array.Copy(payload, 0, report, 1, Math.Min(payload.Length, report.Length - 1));
            }

            // SetReportOutput returns false on transient errors (timeout, stall). We don't
            // retry here because the higher-level read loops already have their own retry
            // logic and timeouts.
            _ = PortalUsb.SetReportOutput(_handle, report);
            return;
        }

        // Original PC USB portal (VID 0x1430 PID 0x1F17). Commands ride on top of the
        // PS3-Move-style framing: 0x0B = "vendor command" report id, 0x14 = sub-type.
        byte[] packet = new byte[32];
        packet[0] = 0x0B;
        packet[1] = 0x14;
        packet[2] = command;
        if (payload is not null)
        {
            Array.Copy(payload, 0, packet, 3, Math.Min(payload.Length, packet.Length - 3));
        }

        PortalUsb.Write(_handle, packet);
    }

    public byte[] Read()
    {
        return PortalUsb.Read(_handle, 32);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }
}

internal enum PortalProtocol
{
    SpyroPcUsb,
    HidStyle
}

internal sealed class HidPortalConnection : IPortalConnection
{
    private readonly HidPortal.HidHandle _handle;

    public HidPortalConnection(HidPortal.HidHandle handle, string description, int? productId)
    {
        _handle = handle;
        Description = description;
        ProductId = productId;
    }

    public string Description { get; }
    public int? ProductId { get; }

    public void WriteCommand(byte command, byte[]? payload = null)
    {
        int length = Math.Max(_handle.Capabilities.OutputReportByteLength, 33);
        byte[] packet = new byte[length];
        packet[0] = 0x00;
        packet[1] = command;

        if (payload is not null)
        {
            Array.Copy(payload, 0, packet, 2, Math.Min(payload.Length, packet.Length - 2));
        }

        HidPortal.SetOutputReport(_handle, packet);
    }

    public byte[] Read()
    {
        return HidPortal.Read(_handle);
    }

    public void Dispose()
    {
        _handle.Dispose();
    }
}
