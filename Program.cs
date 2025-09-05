namespace HarmonyBot;

public static class Program
{
	public static async Task Main()
	{
		var cfg = Config.Load();
		var bot = new Bot(cfg);
		await bot.RunAsync();
	}
}