using OllMem.Core.Domain;

namespace OllMem.Core.Abstractions;

/// <summary>
/// Tracks which owner identities are the same real-world caller — e.g. the
/// same person signing requests from two different machines with two
/// different keys. Links are symmetric and transitive: linking A↔B and B↔C
/// makes A, B and C all equivalent for recall purposes. Linking never merges
/// or moves data — each owner keeps its own encrypted namespace; a link only
/// widens what <see cref="IMemoryEngine"/> considers "close enough" to
/// broaden recall into (still never as close as an exact namespace match).
/// </summary>
public interface IOwnerLinkStore
{
    /// <summary>Declare two owners as the same identity (idempotent, symmetric).</summary>
    ValueTask LinkAsync(string ownerA, string ownerB, CancellationToken ct = default);

    /// <summary>Remove a previously declared link between two owners.</summary>
    ValueTask UnlinkAsync(string ownerA, string ownerB, CancellationToken ct = default);

    /// <summary>
    /// The full equivalence set for <paramref name="owner"/>, including the
    /// owner itself, following links transitively.
    /// </summary>
    ValueTask<IReadOnlySet<string>> GetLinkedOwnersAsync(string owner, CancellationToken ct = default);

    /// <summary>
    /// Every declared link, once per pair in canonical form. Intended for
    /// visualisation and administration, not for the recall hot path — use
    /// <see cref="GetLinkedOwnersAsync"/> there.
    /// </summary>
    ValueTask<IReadOnlyList<OwnerLink>> GetAllLinksAsync(CancellationToken ct = default);
}
