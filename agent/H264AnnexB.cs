namespace RemoteAssist.Agent;

internal static class H264AnnexB
{
    public static byte[] Normalize(byte[] data)
    {
        if (data.Length < 4 || StartCodeLength(data, 0) != 0)
            return data;

        if (data[0] == 1 &&
            TryNormalizeAvcConfigurationRecord(data, out var configured))
            return configured;

        using var output = new MemoryStream(data.Length + 64);
        var offset = 0;

        while (offset + 4 <= data.Length)
        {
            var length =
                (data[offset] << 24) |
                (data[offset + 1] << 16) |
                (data[offset + 2] << 8) |
                data[offset + 3];

            offset += 4;
            if (length <= 0 || offset + length > data.Length)
                return data;

            output.Write([0, 0, 0, 1]);
            output.Write(data, offset, length);
            offset += length;
        }

        return offset == data.Length ? output.ToArray() : data;
    }

    private static bool TryNormalizeAvcConfigurationRecord(
        ReadOnlySpan<byte> data,
        out byte[] normalized)
    {
        normalized = [];
        if (data.Length < 7 || data[0] != 1)
            return false;

        try
        {
            using var output = new MemoryStream(data.Length + 32);
            var offset = 5;
            var spsCount = data[offset++] & 0x1F;

            for (var i = 0; i < spsCount; i++)
            {
                if (offset + 2 > data.Length) return false;
                var length = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                if (length <= 0 || offset + length > data.Length) return false;
                output.Write([0, 0, 0, 1]);
                output.Write(data.Slice(offset, length));
                offset += length;
            }

            if (offset >= data.Length) return false;
            var ppsCount = data[offset++];

            for (var i = 0; i < ppsCount; i++)
            {
                if (offset + 2 > data.Length) return false;
                var length = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                if (length <= 0 || offset + length > data.Length) return false;
                output.Write([0, 0, 0, 1]);
                output.Write(data.Slice(offset, length));
                offset += length;
            }

            normalized = output.ToArray();
            return normalized.Length > 0;
        }
        catch
        {
            normalized = [];
            return false;
        }
    }

    public static bool ContainsNalType(ReadOnlySpan<byte> data, int wantedType)
    {
        for (var i = 0; i + 3 < data.Length;)
        {
            var startLength = StartCodeLength(data, i);
            if (startLength == 0)
            {
                i++;
                continue;
            }

            var nalIndex = i + startLength;
            if (nalIndex < data.Length && (data[nalIndex] & 0x1F) == wantedType)
                return true;

            i = nalIndex + 1;
        }

        return false;
    }

    private static int StartCodeLength(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 3 < data.Length &&
            data[offset] == 0 && data[offset + 1] == 0 &&
            data[offset + 2] == 0 && data[offset + 3] == 1)
            return 4;

        if (offset + 2 < data.Length &&
            data[offset] == 0 && data[offset + 1] == 0 &&
            data[offset + 2] == 1)
            return 3;

        return 0;
    }
}
