using System.Security.Cryptography;
using System.Text;

namespace Squill.Core;

public static class HashUtility
{
    public static byte[] Concat(IEnumerable<byte[]> hashes)
    {
        var length = hashes.Sum(i => i.Length);
        var result = new byte[length];
        int index = 0;

        foreach (var hash in hashes)
        {
            hash.CopyTo(result, index);
            index += hash.Length;
        }

        return Compute(result);
    }

    public static byte[] Concat(params byte[][] hashes)
        => Concat((IEnumerable<byte[]>)hashes);
    
    public static byte[] Compute(params string[] values) 
        => Concat(values.Select(i => Compute(Encoding.UTF8.GetBytes(i))));

    public static byte[] Compute(IEnumerable<IHashable> hashables) 
        => Concat(hashables.Select(i => i.Hash));

    public static byte[] Compute(ReadOnlySpan<byte> input) 
        => SHA256.HashData(input);

    public static bool HashesEqual(ReadOnlySpan<byte> hash1, ReadOnlySpan<byte> hash2)
        => hash1.SequenceEqual(hash2);
}