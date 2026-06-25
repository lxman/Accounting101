using System.Security.Cryptography;
using System.Text;

namespace Accounting101.Ledger.Contracts;

/// <summary>
/// Produces a deterministic RFC-4122 version-5 (SHA-1, name-based) UUID for a given source
/// document type and reference. Callers retrying an idempotent POST to the ledger engine
/// derive the same entry id without any coordination.
/// </summary>
public static class EntryIdentity
{
    // Namespace GUID (fixed by spec): b3f1d6c2-7a4e-5b9c-8d0f-1e2a3b4c5d6e
    private static readonly byte[] NamespaceBytes = ToNetworkOrder(
        Guid.Parse("b3f1d6c2-7a4e-5b9c-8d0f-1e2a3b4c5d6e"));

    /// <summary>
    /// Computes a UUIDv5 for <c>{sourceType}:{sourceRef:N}</c> under the Accounting101 namespace.
    /// </summary>
    /// <param name="sourceType">Document type, e.g. "Invoice" or "Bill". Must not be null or empty.</param>
    /// <param name="sourceRef">The document's own GUID.</param>
    /// <returns>A deterministic, RFC-4122-compliant version-5 UUID.</returns>
    public static Guid ForSource(string sourceType, Guid sourceRef)
    {
        if (string.IsNullOrEmpty(sourceType))
            throw new ArgumentException("sourceType must not be null or empty.", nameof(sourceType));

        // name = "{sourceType}:{sourceRef:N}"  (no hyphens in the ref)
        string name = $"{sourceType}:{sourceRef:N}";
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);

        // Hash: SHA1( bigEndian(namespace 16 bytes) || utf8(name) )
        byte[] input = new byte[NamespaceBytes.Length + nameBytes.Length];
        NamespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, NamespaceBytes.Length);

        byte[] hash = SHA1.HashData(input);

        // Take the first 16 bytes of the digest
        byte[] rfc = new byte[16];
        Array.Copy(hash, rfc, 16);

        // Set version nibble to 5 (high nibble of byte 6)
        rfc[6] = (byte)((rfc[6] & 0x0F) | 0x50);

        // Set variant: top two bits of byte 8 to 10xx xxxx
        rfc[8] = (byte)((rfc[8] & 0x3F) | 0x80);

        // Convert RFC-order bytes back to a .NET Guid.
        // .NET Guid(byte[]) reads bytes 0-3 as little-endian, bytes 4-5 as little-endian,
        // bytes 6-7 as little-endian; bytes 8-15 are big-endian (as-is).
        // Our rfc[] bytes are in network (big-endian) order, so reverse the first three fields.
        byte[] dotnet = new byte[16];
        // Field 1: bytes 0-3 reversed
        dotnet[0] = rfc[3]; dotnet[1] = rfc[2]; dotnet[2] = rfc[1]; dotnet[3] = rfc[0];
        // Field 2: bytes 4-5 reversed
        dotnet[4] = rfc[5]; dotnet[5] = rfc[4];
        // Field 3: bytes 6-7 reversed
        dotnet[6] = rfc[7]; dotnet[7] = rfc[6];
        // Fields 4-5: bytes 8-15 as-is
        for (int i = 8; i < 16; i++) dotnet[i] = rfc[i];

        return new Guid(dotnet);
    }

    /// <summary>
    /// Converts a .NET <see cref="Guid"/> to its 16 big-endian (network-order) bytes.
    /// .NET's <see cref="Guid.ToByteArray()"/> returns the first three fields little-endian;
    /// this reverses those fields so the result matches the RFC wire format.
    /// </summary>
    private static byte[] ToNetworkOrder(Guid guid)
    {
        byte[] le = guid.ToByteArray(); // little-endian mixed layout
        byte[] be = new byte[16];

        // Field 1 (4 bytes): reverse
        be[0] = le[3]; be[1] = le[2]; be[2] = le[1]; be[3] = le[0];
        // Field 2 (2 bytes): reverse
        be[4] = le[5]; be[5] = le[4];
        // Field 3 (2 bytes): reverse
        be[6] = le[7]; be[7] = le[6];
        // Fields 4-5 (8 bytes): as-is
        for (int i = 8; i < 16; i++) be[i] = le[i];

        return be;
    }
}
