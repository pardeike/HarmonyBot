using System.Text.Json;

namespace HarmonyBot;

public sealed class Config
{
	public required string DiscordToken { get; init; }
	public required string OpenAIApiKey { get; init; }

	public string ChatModel { get; init; } = Environment.GetEnvironmentVariable("CHAT_MODEL") ?? "gpt-4o";
	public string? OwnerUserId { get; init; } = Environment.GetEnvironmentVariable("OWNER_USER_ID"); // optional hard lock

	public int ContextBefore { get; init; } = int.TryParse(Environment.GetEnvironmentVariable("CTX_BEFORE"), out var b) ? b : 6;
	public int ContextAfter { get; init; } = int.TryParse(Environment.GetEnvironmentVariable("CTX_AFTER"), out var a) ? a : 2;

	public string? LlmPackDir { get; init; } = Environment.GetEnvironmentVariable("LLM_PACK_DIR");

	public static Config Load()
	{
		var path = Environment.GetEnvironmentVariable("API_KEYS_FILE");
		if (string.IsNullOrWhiteSpace(path))
			path = "~/.api-keys";
		if (path.StartsWith('~'))
			path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);

		if (!File.Exists(path))
			throw new FileNotFoundException($"Cannot find API keys file at {path}. Expected JSON: {{\"DISCORD_TOKEN\":\"...\",\"OPENAI_API_KEY\":\"...\"}}");

		var json = JsonDocument.Parse(File.ReadAllText(path));
		var root = json.RootElement;
		var discord = root.GetProperty("DISCORD_TOKEN").GetString() ?? "";
		var openai = root.GetProperty("OPENAI_API_KEY").GetString() ?? "";

		if (string.IsNullOrWhiteSpace(discord) || string.IsNullOrWhiteSpace(openai))
			throw new InvalidOperationException("~/.api-keys is missing DISCORD_TOKEN or OPENAI_API_KEY");

		return new Config { DiscordToken = discord, OpenAIApiKey = openai };
	}
}
