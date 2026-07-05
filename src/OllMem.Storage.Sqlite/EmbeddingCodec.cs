using System.Runtime.InteropServices;

namespace OllMem.Storage.Sqlite;

/// <summary>Serialises embedding vectors to/from a compact little-endian blob.</summary>
internal static class EmbeddingCodec
{
    public static byte[] ToBytes(float[] vector) =>
        MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] FromBytes(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * sizeof(float));
        return floats;
    }
}
