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
    public const uint DisconnectedReleaseFlag = 1;

    public byte[] Encode()
    {
        var buffer = new byte[WireSize];
        WriteTo(buffer);
        return buffer;
    }

    public void WriteTo(Span<byte> buffer)
    {
        if (buffer.Length < WireSize) throw new ArgumentException("Input packet buffer is too short", nameof(buffer));
        buffer[0] = (byte)Type;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), Value1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(5, 4), Value2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(9, 4), Flags);
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
    public const byte BatchMessage = 2;
    public const byte PingMessage = 3;
    public const string BatchSubProtocol = "duotrack.v2";
    public const int MaximumBatch = 64;
    public const int InputWireSize = 1 + 16 + InputPacket.WireSize;

    public static byte[] EncodeInput(Guid targetDeviceId, InputPacket packet)
    {
        var buffer = new byte[InputWireSize];
        buffer[0] = InputMessage;
        targetDeviceId.TryWriteBytes(buffer.AsSpan(1, 16));
        packet.WriteTo(buffer.AsSpan(17, InputPacket.WireSize));
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

    public static byte[] EncodeBatch(Guid peer, ReadOnlySpan<InputPacket> packets)
    {
        if (packets.Length is < 1 or > MaximumBatch) throw new ArgumentOutOfRangeException(nameof(packets));
        var buffer = new byte[19 + packets.Length * InputPacket.WireSize];
        buffer[0] = BatchMessage;
        peer.TryWriteBytes(buffer.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(17), (ushort)packets.Length);
        for (var i = 0; i < packets.Length; i++) packets[i].WriteTo(buffer.AsSpan(19 + i * InputPacket.WireSize));
        return buffer;
    }

    public static bool TryDecodeBatch(ReadOnlySpan<byte> data, out Guid peer, out InputPacket[] packets)
    {
        peer = default; packets = [];
        if (data.Length < 19 || data[0] != BatchMessage) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(17, 2));
        if (count is < 1 or > MaximumBatch || data.Length != 19 + count * InputPacket.WireSize) return false;
        var decoded = new InputPacket[count];
        for (var i = 0; i < count; i++)
            if (!InputPacket.TryDecode(data.Slice(19 + i * InputPacket.WireSize, InputPacket.WireSize), out decoded[i])) return false;
        peer = new Guid(data.Slice(1, 16));
        packets = decoded;
        return true;
    }

    public static byte[] EncodePing(long timestamp)
    {
        var buffer = new byte[9]; buffer[0] = PingMessage;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(1), timestamp);
        return buffer;
    }

    public static bool TryDecodePing(ReadOnlySpan<byte> data, out long timestamp)
    {
        timestamp = 0;
        if (data.Length != 9 || data[0] != PingMessage) return false;
        timestamp = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(1));
        return true;
    }
}
