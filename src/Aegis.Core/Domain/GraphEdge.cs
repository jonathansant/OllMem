namespace Aegis.Core.Domain;

/// <summary>
/// A directed, labelled relationship in the knowledge graph
/// (PRD REQ-3.1), e.g. User --"works at"--&gt; Company.
/// </summary>
/// <param name="Subject">Entity the fact is about (the graph node key).</param>
/// <param name="Predicate">The relationship label, normalised to lower case.</param>
/// <param name="Object">The related entity / value.</param>
public readonly record struct GraphEdge(string Subject, string Predicate, string Object)
{
    public static GraphEdge Create(string subject, string predicate, string @object) =>
        new(Normalize(subject), Normalize(predicate), @object.Trim());

    /// <summary>Identity of the (subject, predicate) pair used for conflict checks.</summary>
    public string RelationKey => $"{Subject}|{Predicate}";

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
