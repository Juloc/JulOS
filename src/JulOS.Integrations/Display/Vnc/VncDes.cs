namespace JulOS.Integrations.Display.Vnc;

// The DES of the classic VNC password sign-in (FIPS 46-3, single DES, ECB, one block at a time). .NET's own DES is not used: with OpenSSL 3 it
// belongs to the legacy provider, which a server often does not load. The sign-in has one oddity: every byte of the key is bit-reversed first.
internal static class VncDes
{
    private static readonly byte[] InitialPermutation =
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4, 62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3, 61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    ];

    private static readonly byte[] Expansion =
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    ];

    private static readonly byte[] Permutation =
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10, 2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    ];

    private static readonly byte[] KeyPermutation1 =
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4,
    ];

    private static readonly byte[] KeyPermutation2 =
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4, 26, 8, 16, 7, 27, 20, 13, 2,
        41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    ];

    private static readonly byte[] Shifts = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    private static readonly byte[][] SBoxes =
    [
        [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7, 0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8, 4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0, 15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
        [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10, 3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5, 0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15, 13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
        [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8, 13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1, 13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7, 1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
        [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15, 13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9, 10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4, 3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
        [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9, 14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6, 4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14, 11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
        [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11, 10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8, 9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6, 4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
        [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1, 13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6, 1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2, 6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
        [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7, 1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2, 7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8, 2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11],
    ];

    // The sign-in: the first eight characters of the password (Latin-1, padded with zero bytes) are the key, with every byte's bits in reverse order;
    // the 16-byte challenge is encrypted as two blocks.
    public static byte[] Response(string password, ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 16)
        {
            throw new ArgumentException("The challenge has 16 bytes.", nameof(challenge));
        }

        var key = new byte[8];
        for (var i = 0; i < 8 && i < password.Length; i++)
        {
            key[i] = ReverseBits(password[i] > 255 ? (byte)'?' : (byte)password[i]);
        }

        var result = new byte[16];
        EncryptBlock(key, challenge[..8], result.AsSpan(0, 8));
        EncryptBlock(key, challenge[8..], result.AsSpan(8, 8));
        return result;
    }

    private static byte ReverseBits(byte value)
    {
        var result = 0;
        for (var i = 0; i < 8; i++)
        {
            result = (result << 1) | ((value >> i) & 1);
        }

        return (byte)result;
    }

    // One block, the key as it is (no bit reversal): the plain DES, which the tests check against the published test vector.
    public static void EncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block, Span<byte> output)
    {
        var subkeys = Schedule(key);
        var bits = Permute(ToBits(block), InitialPermutation);
        var left = bits[..32];
        var right = bits[32..];
        for (var round = 0; round < 16; round++)
        {
            var mixed = Feistel(right, subkeys[round]);
            var next = new byte[32];
            for (var i = 0; i < 32; i++)
            {
                next[i] = (byte)(left[i] ^ mixed[i]);
            }

            left = right;
            right = next;
        }

        // The halves swap once more, then the inverse of the initial permutation.
        var joined = new byte[64];
        right.CopyTo(joined, 0);
        left.CopyTo(joined, 32);
        var final = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            final[InitialPermutation[i] - 1] = joined[i];
        }

        for (var i = 0; i < 8; i++)
        {
            var value = 0;
            for (var j = 0; j < 8; j++)
            {
                value = (value << 1) | final[i * 8 + j];
            }

            output[i] = (byte)value;
        }
    }

    private static byte[] ToBits(ReadOnlySpan<byte> bytes)
    {
        var bits = new byte[bytes.Length * 8];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)((bytes[i / 8] >> (7 - i % 8)) & 1);
        }

        return bits;
    }

    private static byte[] Permute(byte[] input, byte[] table)
    {
        var output = new byte[table.Length];
        for (var i = 0; i < table.Length; i++)
        {
            output[i] = input[table[i] - 1];
        }

        return output;
    }

    private static byte[][] Schedule(ReadOnlySpan<byte> key)
    {
        var permuted = Permute(ToBits(key), KeyPermutation1);
        var c = permuted[..28];
        var d = permuted[28..];
        var keys = new byte[16][];
        for (var round = 0; round < 16; round++)
        {
            c = RotateLeft(c, Shifts[round]);
            d = RotateLeft(d, Shifts[round]);
            keys[round] = Permute([.. c, .. d], KeyPermutation2);
        }

        return keys;
    }

    private static byte[] RotateLeft(byte[] bits, int count)
    {
        var result = new byte[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            result[i] = bits[(i + count) % bits.Length];
        }

        return result;
    }

    private static byte[] Feistel(byte[] right, byte[] subkey)
    {
        var expanded = Permute(right, Expansion);
        var output = new byte[32];
        for (var box = 0; box < 8; box++)
        {
            var at = box * 6;
            for (var i = 0; i < 6; i++)
            {
                expanded[at + i] ^= subkey[at + i];
            }

            var row = (expanded[at] << 1) | expanded[at + 5];
            var column = (expanded[at + 1] << 3) | (expanded[at + 2] << 2) | (expanded[at + 3] << 1) | expanded[at + 4];
            var value = SBoxes[box][row * 16 + column];
            for (var i = 0; i < 4; i++)
            {
                output[box * 4 + i] = (byte)((value >> (3 - i)) & 1);
            }
        }

        return Permute(output, Permutation);
    }
}
