namespace Aegis.Core.Domain;

/// <summary>
/// Hierarchical memory tiers (PRD REQ-1.1). Memories are scoped into three
/// distinct levels so that context can be composed from the most stable
/// (Agent persona) down to the most volatile (Session task state).
/// </summary>
public enum MemoryScope
{
    /// <summary>Core persona / standing instructions for an agent.</summary>
    Agent = 0,

    /// <summary>Global, long-lived preferences and facts about a user.</summary>
    User = 1,

    /// <summary>Ephemeral state tied to the current task / conversation.</summary>
    Session = 2
}
