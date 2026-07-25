namespace OllMem.Core.Domain;

/// <summary>
/// A declared equivalence between two owner identities — the same real-world
/// caller signing from two machines/keys. Canonical form: <paramref name="OwnerA"/>
/// sorts before <paramref name="OwnerB"/>, both lower-cased, so a pair has one
/// representation regardless of which side declared it.
/// </summary>
public readonly record struct OwnerLink(string OwnerA, string OwnerB)
{
    public static OwnerLink Create(string ownerA, string ownerB)
    {
        var a = ownerA.Trim().ToLowerInvariant();
        var b = ownerB.Trim().ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? new OwnerLink(a, b) : new OwnerLink(b, a);
    }
}
