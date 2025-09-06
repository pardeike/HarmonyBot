using Microsoft.Extensions.Logging;

namespace HarmonyBot;

public static class LogSetup
{
	public static ILoggerFactory CreateLoggerFactory()
	{
		var format = (Environment.GetEnvironmentVariable("LOG_FORMAT") ?? "text").ToLowerInvariant();
		var minLvl = Environment.GetEnvironmentVariable("LOG_LEVEL");
		var level = Enum.TryParse<LogLevel>(minLvl, true, out var l) ? l : LogLevel.Information;

		return LoggerFactory.Create(builder =>
		{
			_ = builder.SetMinimumLevel(level);

			if (format == "text")
			{
				_ = builder.AddSimpleConsole(o =>
				{
					o.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
					o.SingleLine = true;
				});
			}
			else
			{
				_ = builder.AddJsonConsole(o =>
				{
					o.IncludeScopes = true;
					o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff 'UTC'";
				});
			}
		});
	}
}
