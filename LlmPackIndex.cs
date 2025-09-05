using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarmonyBot;

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

	public LlmPackIndex(List<Card> cards) => _cards = cards;

	public static async Task<bool> DownloadCards(Config cfg, ILogger log)
	{
		_ = Directory.CreateDirectory(cfg.LlmPackDir);
		var jsonl = Path.Combine(cfg.LlmPackDir, "harmony.cards.jsonl");
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
		using var resp = await http.GetAsync(cfg.LlmPackUri, HttpCompletionOption.ResponseHeadersRead);
		log.LogInformation("LLM pack download: {status}", resp.StatusCode);
		if (!resp.IsSuccessStatusCode)
			return false;
		await using var contentStream = await resp.Content.ReadAsStreamAsync();
		await using var fs = new FileStream(jsonl, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
		await contentStream.CopyToAsync(fs);
		await fs.FlushAsync();
		log.LogInformation("LLM pack saved to {Dest}", jsonl);
		return true;
	}

	public static LlmPackIndex LoadAsync(Config cfg, ILogger log)
	{
		var jsonl = Path.Combine(cfg.LlmPackDir, "harmony.cards.jsonl");
		if (File.Exists(jsonl))
		{
			var cards = new List<Card>(capacity: 2048);
			foreach (var line in File.ReadLines(jsonl))
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
		return new LlmPackIndex([]);
	}

	// Naive lexical top‑k (fast + no embeddings needed). Good enough as a hint layer.
	public IReadOnlyList<Card> Search(string query, int k)
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
