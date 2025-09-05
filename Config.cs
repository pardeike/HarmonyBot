using System.Text.Json;

namespace HarmonyBot;

public sealed class Config
{
	public required string DiscordToken { get; init; }
	public required string OpenAIApiKey { get; init; }

	public string ChatModel { get; init; } = GetEnvString("CHAT_MODEL", "gpt-4o");
	public string? OwnerUserId { get; init; } = GetEnvString("OWNER_USER_ID", "");

	public int ContextBefore { get; init; } = GetEnvInt("CTX_BEFORE", 6);
	public int ContextAfter { get; init; } = GetEnvInt("CTX_AFTER", 2);

	public string LlmPackDir { get; init; } = GetEnvString("LLM_PACK_DIR", "");

	public int GroupMaxGapSec { get; init; } = GetEnvInt("GROUP_MAX_GAP_SEC", 300); // 5 min
	public int GroupMaxDurationSec { get; init; } = GetEnvInt("GROUP_MAX_DURATION_SEC", 1800); // 30 min
	public int GroupMaxInterposts { get; init; } = GetEnvInt("GROUP_MAX_INTERPOSTS", 6);
	public int CtxPrependBefore { get; init; } = GetEnvInt("CTX_PREPEND_BEFORE", 3);
	public int CtxMaxMessages { get; init; } = GetEnvInt("CTX_MAX_MESSAGES", 60);
	public int CtxMaxChars { get; init; } = GetEnvInt("CTX_MAX_CHARS", 12000);

	public bool IncludeInterposts { get; init; } = GetEnvBool("CTX_INCLUDE_INTERPOSTS", false);

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

	private static string GetEnvString(string name, string defaultValue) => Environment.GetEnvironmentVariable(name) ?? defaultValue;
	private static int GetEnvInt(string name, int defaultValue) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : defaultValue;
	private static bool GetEnvBool(string name, bool defaultValue) => string.Equals(GetEnvString(name, defaultValue ? "true" : "false"), "true", StringComparison.OrdinalIgnoreCase);
}
