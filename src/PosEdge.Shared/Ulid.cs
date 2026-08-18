using System.Buffers.Text;
using System.Security.Cryptography;

namespace PosEdge.Shared;

/// <summary>
/// Minimal ULID generator for request/message correlation.
/// Uses 48-bit millisecond timestamp + 80 bits of crypto randomness.
/// </summary>
public static class Ulid
{
    public static string NewUlidString()
    {
        Span<byte> bytes = stackalloc byte[16];
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // 48-bit timestamp big-endian
        bytes[0] = (byte)((ts >> 40) & 0xFF);
        bytes[1] = (byte)((ts >> 32) & 0xFF);
        bytes[2] = (byte)((ts >> 24) & 0xFF);
        bytes[3] = (byte)((ts >> 16) & 0xFF);
        bytes[4] = (byte)((ts >> 8) & 0xFF);
        bytes[5] = (byte)(ts & 0xFF);
        RandomNumberGenerator.Fill(bytes[6..]);
        return EncodeCrockfordBase32(bytes);
    }

    // Crockford Base32 alphabet (no I,L,O,U).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string EncodeCrockfordBase32(ReadOnlySpan<byte> data16)
    {
        // ULID is 128 bits -> 26 chars base32 (130 bits; leading bits are zero).
        Span<char> outChars = stackalloc char[26];
        ulong hi = ((ulong)data16[0] << 56) | ((ulong)data16[1] << 48) | ((ulong)data16[2] << 40) | ((ulong)data16[3] << 32) |
                   ((ulong)data16[4] << 24) | ((ulong)data16[5] << 16) | ((ulong)data16[6] << 8) | data16[7];
        ulong lo = ((ulong)data16[8] << 56) | ((ulong)data16[9] << 48) | ((ulong)data16[10] << 40) | ((ulong)data16[11] << 32) |
                   ((ulong)data16[12] << 24) | ((ulong)data16[13] << 16) | ((ulong)data16[14] << 8) | data16[15];

        // 26 groups of 5 bits. We pull from a 128-bit value; easiest by manual shifting.
        // We'll build a 130-bit buffer with 2 leading zero bits.
        // index 0 reads bits 129..125 (zeros + top of hi).
        // To avoid BigInteger, read bits by position.
        for (int i = 0; i < 26; i++)
        {
            int bitPos = 130 - 5 * (i + 1); // start bit index (0..129), where 129 is MSB of 130-bit buffer
            int val = Read5Bits(hi, lo, bitPos);
            outChars[i] = Alphabet[val];
        }

        return new string(outChars);
    }

    private static int Read5Bits(ulong hi, ulong lo, int bitPos)
    {
        // bitPos refers to 130-bit buffer with 2 leading zeros then 128 bits (hi||lo).
        // Map to 128-bit index: idx128 = bitPos - 2. If idx128 < 0 => leading zeros.
        int idx128 = bitPos - 2;
        int v = 0;
        for (int j = 0; j < 5; j++)
        {
            int b = idx128 + j;
            int bit = 0;
            if (b >= 0 && b < 128)
            {
                if (b < 64)
                    bit = (int)((hi >> (63 - b)) & 1UL);
                else
                    bit = (int)((lo >> (127 - b)) & 1UL);
            }
            v = (v << 1) | bit;
        }
        return v;
    }
}

