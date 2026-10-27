using System.Buffers.Binary;

namespace DuoTrack.Protocol;

public enum InputEventType : byte
{
    Key = 1,
    MouseMove = 2,
    MouseButton = 3,
    MouseWheel = 4,
    ReleaseAll = 5,
    EnterScreen = 6,
    ReturnControl = 7,
}

public enum ScreenSide { Left, Right, Top, Bottom }

public enum MouseButton : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

public readonly record struct InputPacket(
    InputEventType Type,
    int Value1 = 0,
    int Value2 = 0,
    uint Flags = 0)
{
    public const int WireSize = 13;

    public byte[] Encode()
    {
        var buffer = new byte[WireSize];
        buffer[0] = (byte)Type;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(1, 4), Value1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(5, 4), Value2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(9, 4), Flags);
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out InputPacket packet)
    {
        packet = default;
        if (data.Length != WireSize || !Enum.IsDefined((InputEventType)data[0]))
        {
            return false;
        }

        packet = new InputPacket(
            (InputEventType)data[0],
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(1, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(5, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(9, 4)));
        return true;
    }
}

public static class RelayWire
{
    public const byte InputMessage = 1;
    public const int InputWireSize = 1 + 16 + InputPacket.WireSize;

    public static byte[] EncodeInput(Guid targetDeviceId, InputPacket packet)
    {
        var buffer = new byte[InputWireSize];
        buffer[0] = InputMessage;
        targetDeviceId.TryWriteBytes(buffer.AsSpan(1, 16));
        packet.Encode().CopyTo(buffer, 17);
        return buffer;
    }

    public static bool TryDecodeInput(ReadOnlySpan<byte> data, out Guid targetDeviceId, out InputPacket packet)
    {
        targetDeviceId = default;
        packet = default;
        if (data.Length != InputWireSize || data[0] != InputMessage)
        {
            return false;
        }

        targetDeviceId = new Guid(data.Slice(1, 16));
        return InputPacket.TryDecode(data.Slice(17), out packet);
    }
}
