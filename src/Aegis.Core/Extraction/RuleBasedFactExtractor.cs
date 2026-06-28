using System.Text.RegularExpressions;
using Aegis.Core.Abstractions;
using Aegis.Core.Domain;

namespace Aegis.Core.Extraction;

/// <summary>
/// A deterministic, dependency-free implementation of the auto-extraction
/// pipeline (PRD REQ-2.1). It scans first-person utterances for a curated set of
/// fact / preference / state-change patterns and emits normalised triples.
///
/// This is intentionally a baseline: it runs offline with no model weights and
/// gives predictable behaviour for tests. Production deployments register an
/// LLM-backed <see cref="IFactExtractor"/> for open-domain coverage.
/// </summary>
public sealed partial class RuleBasedFactExtractor : IFactExtractor
{
    private const string Subject = "user";

    // Lines prefixed with a non-user speaker are ignored so the agent does not
    // "remember" its own statements as user facts.
    [GeneratedRegex(@"^\s*(?<speaker>[A-Za-z][A-Za-z0-9 _-]{0,20})\s*:\s*(?<body>.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpeakerLine();

    private static readonly HashSet<string> UserSpeakers =
        new(StringComparer.OrdinalIgnoreCase) { "user", "me", "human", "u" };

    private static readonly HashSet<string> NonUserSpeakers =
        new(StringComparer.OrdinalIgnoreCase)
        { "assistant", "ai", "bot", "system", "agent", "aegis", "gpt", "claude" };

    private sealed record Pattern(Regex Regex, string Predicate, Func<string, string> Statement);

    // Object capture stops at clause boundaries so compound sentences don't
    // swallow unrelated text. Ordered most-specific first.
    private static readonly Pattern[] Patterns =
    [
        new(Build(@"my name is\s+(?<obj>.+?)"), "name",
            o => $"User's name is {o}"),

        new(Build(@"my favou?rite\s+(?<key>[a-z][a-z ]*?)\s+is\s+(?<obj>.+?)"), "__favorite__",
            o => o), // handled specially (predicate carries the key)

        new(Build(@"i (?:live in|am from|'m from|moved to|now live in|relocated to|just moved to)\s+(?<obj>.+?)"),
            "lives in", o => $"User lives in {o}"),

        new(Build(@"i (?:work|am working) (?:at|for)\s+(?<obj>.+?)"), "works at",
            o => $"User works at {o}"),

        new(Build(@"i (?:am|'m) (?:an?\s+)(?<obj>.+?)"), "is a",
            o => $"User is a {o}"),

        new(Build(@"i (?:really )?(?:don't like|do not like|dislike|hate|can't stand|cannot stand)\s+(?<obj>.+?)"),
            "dislikes", o => $"User dislikes {o}"),

        new(Build(@"i (?:really )?(?:like|love|enjoy|prefer|am into)\s+(?<obj>.+?)"), "likes",
            o => $"User likes {o}"),

        new(Build(@"i (?:have an?|own an?|own|have)\s+(?<obj>.+?)"), "has",
            o => $"User has {o}"),

        new(Build(@"i (?:use|work with)\s+(?<obj>.+?)"), "uses",
            o => $"User uses {o}"),
    ];

    public ValueTask<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string transcript,
        CancellationToken ct = default)
    {
        var facts = new List<ExtractedFact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(transcript))
            return ValueTask.FromResult<IReadOnlyList<ExtractedFact>>(facts);

        var transcriptHasSpeakers = HasRecognisedSpeakers(transcript);

        foreach (var rawLine in transcript.Split('\n'))
        {
            if (!TryGetUserUtterance(rawLine, transcriptHasSpeakers, out var utterance))
                continue;

            foreach (var sentence in SplitSentences(utterance))
            {
                foreach (var pattern in Patterns)
                {
                    var match = pattern.Regex.Match(sentence);
                    if (!match.Success)
                        continue;

                    var obj = CleanObject(match.Groups["obj"].Value);
                    if (obj.Length == 0)
                        continue;

                    var (predicate, statement) = BuildFact(pattern, match, obj);
                    var edge = GraphEdge.Create(Subject, predicate, obj);

                    if (seen.Add(edge.RelationKey + "=>" + edge.Object.ToLowerInvariant()))
                        facts.Add(new ExtractedFact(statement, edge));
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<ExtractedFact>>(facts);
    }

    private static (string Predicate, string Statement) BuildFact(Pattern pattern, Match match, string obj)
    {
        if (pattern.Predicate == "__favorite__")
        {
            var key = CleanObject(match.Groups["key"].Value).ToLowerInvariant();
            return ($"favorite {key}", $"User's favorite {key} is {obj}");
        }

        return (pattern.Predicate, pattern.Statement(obj));
    }

    private static bool HasRecognisedSpeakers(string transcript)
    {
        foreach (var line in transcript.Split('\n'))
        {
            var m = SpeakerLine().Match(line);
            if (m.Success)
            {
                var speaker = m.Groups["speaker"].Value.Trim();
                if (UserSpeakers.Contains(speaker) || NonUserSpeakers.Contains(speaker))
                    return true;
            }
        }

        return false;
    }

    private static bool TryGetUserUtterance(string rawLine, bool transcriptHasSpeakers, out string utterance)
    {
        utterance = rawLine.Trim();
        if (utterance.Length == 0)
            return false;

        var m = SpeakerLine().Match(rawLine);
        if (m.Success)
        {
            var speaker = m.Groups["speaker"].Value.Trim();
            if (NonUserSpeakers.Contains(speaker))
                return false;
            if (UserSpeakers.Contains(speaker))
            {
                utterance = m.Groups["body"].Value.Trim();
                return utterance.Length > 0;
            }
        }

        // Unprefixed line: only trust it when the transcript has no speaker
        // structure at all (otherwise it's likely a continuation we can't attribute).
        return !transcriptHasSpeakers || !m.Success;
    }

    private static IEnumerable<string> SplitSentences(string utterance) =>
        utterance
            .Split(['.', '!', '?', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    private static string CleanObject(string value)
    {
        var trimmed = value.Trim().TrimEnd('.', ',', '!', '?', ';', ':').Trim();
        // Collapse internal whitespace runs for stable keys.
        return WhitespaceRuns().Replace(trimmed, " ");
    }

    private static Regex Build(string verbPattern)
    {
        // Each pattern is searched anywhere in a sentence; the object runs to the
        // next clause boundary so "I live in NY and work at Google" keeps "NY".
        const string stop = @"(?=$| and | but | because | so | while | although |,)";
        return new Regex($@"\b{verbPattern}{stop}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRuns();
}
