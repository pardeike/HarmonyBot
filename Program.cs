namespace HarmonyBot;

public static class Program
{
	public static async Task Main()
	{
		var cfg = Config.Load();
		using var bot = new Bot(cfg);
		await bot.RunAsync();
	}
}
