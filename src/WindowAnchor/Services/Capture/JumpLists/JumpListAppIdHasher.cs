using System.Text;

namespace WindowAnchor.Services;

/// <summary>Pure Windows-shell AppID hashing used to locate automatic destination files.</summary>
internal static class JumpListAppIdHasher
{
    private static readonly ulong[] Table = BuildTable();

    internal static ulong Compute(string appId)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(appId.ToLowerInvariant());
        ulong crc = 0;
        foreach (byte value in bytes)
            crc = (crc >> 8) ^ Table[(crc ^ value) & 0xFF];
        return crc;
    }

    private static ulong[] BuildTable()
    {
        const ulong polynomial = 0xAD93D23594C935A9UL;
        var table = new ulong[256];
        for (uint index = 0; index < table.Length; index++)
        {
            ulong crc = index;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            table[index] = crc;
        }
        return table;
    }
}
