using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarmonyBot.RAG;

public sealed class LlmPackIndex
{
	public sealed record Card(
		[property: JsonPropertyName("id")] string Id,
		[property: JsonPropertyName("kind")] string Kind,
		[property: JsonPropertyName("summary")] string? Summary,
		[property: JsonPropertyName("signature")] string? Signature,
		[property: JsonPropertyName("remarks")] string? Remarks,
		[property: JsonPropertyName("doc_url")] string? DocUrl,
		[property: JsonPropertyName("examples")] List<Example>? Examples)
	{
		public string CanonicalText =>
			$"{Signature}\n{Summary}\n{Remarks}\n{string.Join("\n", (Examples ?? []).Take(1).Select(e => e.Code ?? ""))}";
	}

	public sealed record Example(
		[property: JsonPropertyName("title")] string? Title,
		[property: JsonPropertyName("code")] string? Code);

	private readonly List<Card> _cards;
	public bool IsLoaded => _cards.Count > 0;

	private LlmPackIndex(List<Card> cards) => _cards = cards;

	public static LlmPackIndex TryLoad(string? hintDir = null)
	{
		static IEnumerable<string> Candidates(string? hint)
		{
			if (!string.IsNullOrWhiteSpace(hint))
				yield return hint;
			// common local paths
			yield return "./llm-pack";
			yield return "../Harmony/llm-pack";
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			yield return Path.Combine(home, "Harmony", "llm-pack");
		}

		foreach (var dir in Candidates(hintDir))
		{
			try
			{
				var jsonl = Path.Combine(dir, "harmony.cards.jsonl");
				if (!File.Exists(jsonl))
					continue;
				var cards = new List<Card>(capacity: 2048);
				foreach (var line in File.ReadLines(jsonl))
				{
					if (string.IsNullOrWhiteSpace(line))
						continue;
					var c = JsonSerializer.Deserialize<Card>(line);
					if (c is not null)
						cards.Add(c);
				}
				return new LlmPackIndex(cards);
			}
			catch { /* ignore and continue */ }
		}
		return new LlmPackIndex([]);
	}

	// Naive lexical top‑k (fast + no embeddings needed). Good enough as a hint layer.
	public IReadOnlyList<Card> Search(string query, int k = 5)
	{
		if (!IsLoaded)
			return [];
		var terms = query.ToLowerInvariant().Split([' ', '\t', '\r', '\n', '.', ',', '(', ')', '[', ']', ':', ';', '#', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
		float Score(Card c)
		{
			var s = (c.CanonicalText ?? "").ToLowerInvariant();
			var hits = 0;
			foreach (var t in terms)
				if (s.Contains(t))
					hits++;
			// small bias for methods/properties
			if (c.Kind is "method" or "property")
				hits += 1;
			return hits;
		}
		return [.. _cards.Select(c => (c, sc: Score(c)))
					 .Where(t => t.sc > 0)
					 .OrderByDescending(t => t.sc)
					 .Take(k)
					 .Select(t => t.c)];
	}
}