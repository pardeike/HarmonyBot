using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarmonyBot;

public sealed class LlmPackIndex(List<LlmPackIndex.Card> cards)
{
	private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
	{
		"a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "do", "does", "for", "from", "get", "has", "have", "how",
		"i", "if", "in", "is", "it", "me", "my", "not", "of", "on", "or", "so", "that", "the", "this", "to", "up", "was", "we",
		"what", "when", "where", "why", "with", "you", "your"
	};

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

	public bool IsLoaded => cards.Count > 0;

	public static async Task<LlmPackIndex> LoadAsync(Config cfg, ILogger log)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
		using var resp = await http.GetAsync(cfg.LlmPackUri, HttpCompletionOption.ResponseHeadersRead);
		if (!resp.IsSuccessStatusCode)
			return new LlmPackIndex([]);
		var jsonl = await resp.Content.ReadAsStringAsync();

		var cards = new List<Card>(capacity: 2048);
		foreach (var line in jsonl.Split("\n"))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;
			var c = JsonSerializer.Deserialize<Card>(line);
			if (c is not null)
				cards.Add(c);
		}
		log.LogInformation("LLM pack created {cardCount} cards", cards.Count);
		return new LlmPackIndex(cards);
	}

	// Naive lexical top‑k (fast + no embeddings needed). Good enough as a hint layer.
	public IReadOnlyList<Card> Search(string query, int k)
	{
		if (!IsLoaded)
			return [];
		var terms = query.ToLowerInvariant()
			.Split([' ', '\t', '\r', '\n', '.', ',', '(', ')', '[', ']', ':', ';', '#', '/', '\\', '"', '\'', '`'], StringSplitOptions.RemoveEmptyEntries)
			.Where(t => t.Length > 2 && !StopWords.Contains(t))
			.Distinct()
			.Take(80)
			.ToArray();
		if (terms.Length == 0)
			return [];

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
		return [.. cards.Select(c => (c, sc: Score(c)))
					 .Where(t => t.sc > 0)
					 .OrderByDescending(t => t.sc)
					 .Take(k)
					 .Select(t => t.c)];
	}
}
