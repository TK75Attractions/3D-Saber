using System;

public enum BleBridgeConnectionState
{
    Unknown,
    Searching,
    NotFound,
    DeviceFound,
    Connecting,
    Connected,
    NotificationsActive,
    Disconnected,
}

public readonly struct BleBridgeStatusUpdate
{
    public readonly bool IsBridgeReady;
    public readonly BleBridgeConnectionState State;
    public readonly string DeviceName;
    public readonly SaberSide Side;

    public BleBridgeStatusUpdate(
        bool isBridgeReady,
        BleBridgeConnectionState state,
        string deviceName)
        : this(isBridgeReady, state, deviceName, SaberSide.Unknown)
    {
    }

    public BleBridgeStatusUpdate(
        bool isBridgeReady,
        BleBridgeConnectionState state,
        string deviceName,
        SaberSide side)
    {
        IsBridgeReady = isBridgeReady;
        State = state;
        DeviceName = deviceName ?? string.Empty;
        Side = side;
    }
}

public static class BleBridgeStatusParser
{
    public static bool TryParse(string message, out BleBridgeStatusUpdate update)
    {
        update = default;
        if (string.Equals(message, "STATE:BRIDGE_READY", StringComparison.Ordinal))
        {
            update = new BleBridgeStatusUpdate(true, BleBridgeConnectionState.Unknown, "");
            return true;
        }

        if (!message.StartsWith("STATE:", StringComparison.Ordinal)) return false;
        string[] values = message.Split(new[] { ':' }, 5);
        if (values.Length < 3 || !values[1].Equals("BLE", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int offset = 2;
        SaberSide side = SaberSide.Unknown;
        if (values.Length >= 4 && SwingPacketParser.TryParseSide(values[2], out SaberSide parsedSide))
        {
            side = parsedSide;
            offset = 3;
        }
        if (!TryParseState(values[offset], out BleBridgeConnectionState state)) return false;
        string name = values.Length > offset + 1 ? values[offset + 1] : string.Empty;
        update = new BleBridgeStatusUpdate(false, state, name, side);
        return true;
    }

    private static bool TryParseState(string value, out BleBridgeConnectionState state)
    {
        switch (value.Trim().ToUpperInvariant())
        {
            case "SEARCHING": state = BleBridgeConnectionState.Searching; return true;
            case "NOT_FOUND": state = BleBridgeConnectionState.NotFound; return true;
            case "DEVICE_FOUND": state = BleBridgeConnectionState.DeviceFound; return true;
            case "CONNECTING": state = BleBridgeConnectionState.Connecting; return true;
            case "CONNECTED": state = BleBridgeConnectionState.Connected; return true;
            case "NOTIFICATIONS_ACTIVE": state = BleBridgeConnectionState.NotificationsActive; return true;
            case "DISCONNECTED": state = BleBridgeConnectionState.Disconnected; return true;
            default: state = BleBridgeConnectionState.Unknown; return false;
        }
    }

}
