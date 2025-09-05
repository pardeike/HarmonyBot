using Discord.WebSocket;
using OpenAI.Chat;
using System.Text;
using HarmonyBot.RAG;
using Discord;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HarmonyBot;

public sealed class Bot
{
	private readonly Config _cfg;
	private readonly DiscordSocketClient _client;
	private readonly ChatClient _chat;
	private readonly LlmPackIndex _llm;

	// approvalId -> pending payload
	private readonly Dictionary<string, Pending> _pending = [];
	private readonly object _lock = new();

	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _log;

	private readonly string _logAiContent;
	private readonly int _logAiContentMax;

	private sealed record Pending(
	 SocketSlashCommand Slash,  // keep the original interaction handle
	 ulong ChannelId,
	 ulong TargetMessageId,
	 string Draft,
	 ulong RequestedByUserId);

	public Bot(Config cfg)
	{
		_cfg = cfg;

		_client = new DiscordSocketClient(new DiscordSocketConfig
		{
			GatewayIntents =
				GatewayIntents.Guilds |
				GatewayIntents.GuildMessages |
				GatewayIntents.MessageContent, // you must enable Message Content intent in Dev Portal
			AlwaysDownloadUsers = false,
			LogGatewayIntentWarnings = false
		});

		_loggerFactory = LogSetup.CreateLoggerFactory();
		_log = _loggerFactory.CreateLogger<Bot>();

		_logAiContent = (Environment.GetEnvironmentVariable("LOG_AI_CONTENT") ?? "truncated").ToLowerInvariant();
		_logAiContentMax = int.TryParse(Environment.GetEnvironmentVariable("LOG_AI_CONTENT_MAX"), out var n) ? n : 4000;

		_client.Log += msg =>
		{
			_log.Log(MapLevel(msg.Severity), "[{Source}] {Message}", msg.Source, msg.Message);
			if (msg.Exception is not null)
				_log.LogError(msg.Exception, "Discord exception ({Source})", msg.Source);
			return Task.CompletedTask;
		};

		_client.Ready += OnReadyAsync;
		_client.SlashCommandExecuted += OnSlashCommandEntrypointAsync;
		_client.ButtonExecuted += OnButtonAsync;

		// OpenAI client (official SDK)
		_chat = new ChatClient(_cfg.ChatModel, _cfg.OpenAIApiKey); // gpt-4o by default; configurable
																					  // Example use of ChatClient documented here. :contentReference[oaicite:3]{index=3}

		_llm = LlmPackIndex.TryLoad(_cfg.LlmPackDir);
	}

	private static LogLevel MapLevel(LogSeverity s) => s switch
	{
		LogSeverity.Critical => LogLevel.Critical,
		LogSeverity.Error => LogLevel.Error,
		LogSeverity.Warning => LogLevel.Warning,
		LogSeverity.Info => LogLevel.Information,
		LogSeverity.Verbose => LogLevel.Debug,
		LogSeverity.Debug => LogLevel.Trace,
		_ => LogLevel.Information
	};

	private static void Divider(string title, params (string Key, object? Val)[] kv)
	{
		var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'");
		var meta = kv is { Length: > 0 } ? " // " + string.Join(", ", kv.Select(p => $"{p.Key}={p.Val}")) : "";
		Console.WriteLine($"========== {ts} :: {title}{meta} ==========");
	}

	private string ApplyAiLogPolicy(string s) => _logAiContent switch
	{
		"none" => "[suppressed]",
		"full" => s,
		_ => s.Length <= _logAiContentMax ? s : s[.._logAiContentMax] + " …"
	};

	private static string FlattenMessages(IEnumerable<OpenAI.Chat.ChatMessage> msgs)
	{
		var sb = new StringBuilder();
		foreach (var m in msgs)
		{
			var tag = m switch
			{
				SystemChatMessage => "system",
				UserChatMessage => "user",
				AssistantChatMessage => "assistant",
				_ => "message"
			};
			var text = string.Concat(m.Content.Select(p => p.Text));
			sb.AppendLine($"[{tag}] {text}");
		}
		return sb.ToString();
	}

	private Task OnSlashCommandEntrypointAsync(SocketSlashCommand cmd)
	{
		_ = Task.Run(async () =>
		{
			try
			{ await OnSlashCommandAsync(cmd); }
			catch (Exception ex)
			{
				_log.LogError(ex, "Unhandled exception in slash handler {Command}", cmd.CommandName);
				try
				{ await SafeErrorAsync(cmd, "Unexpected error. Check logs."); }
				catch { }
			}
		});
		return Task.CompletedTask;
	}

	private static async Task SafeErrorAsync(SocketSlashCommand cmd, string msg)
	{
		// If we haven’t responded yet, this will fail after 3s—best effort only.
		try
		{ await cmd.RespondAsync(msg, ephemeral: true); }
		catch { /* ignore */ }
	}

	public async Task RunAsync()
	{
		await _client.LoginAsync(TokenType.Bot, _cfg.DiscordToken);
		await _client.StartAsync();
		await Task.Delay(-1);
	}

	private async Task OnReadyAsync()
	{
		// Register a GUILD command in each guild for immediate availability (global can take a while).
		// Ephemeral replies use RespondAsync(..., ephemeral: true). :contentReference[oaicite:4]{index=4}
		var cmd = new SlashCommandBuilder()
			.WithName("answer")
			.WithDescription("Searches recent posts, drafts an answer, and asks you to approve.")
			.AddOption(new SlashCommandOptionBuilder()
				.WithName("who")
				.WithDescription("Part of the target user's name (nick/global/username)")
				.WithRequired(true)
				.WithType(ApplicationCommandOptionType.String))
			.Build();

		foreach (var g in _client.Guilds)
		{
			try
			{ await _client.Rest.CreateGuildCommand(cmd, g.Id); }
			catch (Exception ex) { Console.WriteLine($"Failed to create command in {g.Name}: {ex.Message}"); }
		}

		Console.WriteLine($"Logged in as {_client.CurrentUser} — ready.");
	}

	private bool IsOwner(SocketSlashCommand cmd)
	{
		if (string.IsNullOrWhiteSpace(_cfg.OwnerUserId))
			return true; // owner lock not configured
		return ulong.TryParse(_cfg.OwnerUserId, out var ownerId) && cmd.User.Id == ownerId;
	}

	private static string Clamp(string s, int max = 1900) => s.Length <= max ? s : s[..max] + " …";

	private async Task OnSlashCommandAsync(SocketSlashCommand cmd)
	{
		await cmd.DeferAsync(ephemeral: true);

		var who = cmd.Data.Options.First().Value?.ToString()?.Trim() ?? "";
		var guildId = (cmd.GuildId ?? 0UL);
		var channelId = cmd.ChannelId ?? 0;

		using var scope = _log.BeginScope(new Dictionary<string, object>
		{
			["interaction"] = cmd.Id.ToString(),
			["guild"] = guildId,
			["channel"] = channelId,
			["invoker"] = cmd.User.Id,
			["who"] = who
		});

		Divider("/answer start", ("who", who), ("invoker", cmd.User.Username), ("guild", guildId), ("channel", channelId)); // LOG
		_log.LogInformation("answer.start who={who}", who); // LOG

		if (!IsOwner(cmd))
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = "Owner‑only."); _log.LogWarning("answer.denied owner-only"); return; }
		if (string.IsNullOrEmpty(who))
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = "Provide a name fragment."); _log.LogWarning("answer.bad_request empty-who"); return; }
		if (cmd.Channel is not SocketTextChannel chan)
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = "Run /answer in a server text channel."); _log.LogWarning("answer.bad_request not-text-channel"); return; }

		var swTotal = Stopwatch.StartNew();

		var (target, context) = await FindTargetAndContextAsync(chan, who, _cfg.ContextBefore, _cfg.ContextAfter);
		if (target is null)
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = $"No recent message found for “{who}”."); _log.LogInformation("answer.no_target"); return; }

		var targetUser = (target.Author as SocketGuildUser);
		var targetName = targetUser?.DisplayName ?? target.Author.GlobalName ?? target.Author.Username;

		_log.LogInformation("answer.target found target_message={msgId} target_user={user} context_count={count}",
			 target.Id, targetName, context.Count); // LOG

		var contextBlock = BuildContextBlock(context, target);
		var ragHints = BuildRagBlock(contextBlock, out var ragHits);
		var sys = await File.ReadAllTextAsync("Prompts/SystemPrompt.txt");

		var messages = new List<OpenAI.Chat.ChatMessage>
	 {
		  new SystemChatMessage(sys),
		  new SystemChatMessage(ragHints),
		  new UserChatMessage($$"""
            Channel excerpts (oldest → newest):
            {{contextBlock}}

            Target to answer: {{targetName}} (message id: {{target.Id}})
            Task: Write a concise, helpful reply addressing {{targetName}}'s post directly.
            """)
	 };

		var promptText = FlattenMessages(messages);
		_log.LogInformation("ai.request model={model} prompt_chars={chars} rag_hits={hits}\n{preview}",
			 _cfg.ChatModel, promptText.Length, ragHits, ApplyAiLogPolicy(promptText)); // LOG

		var swAi = Stopwatch.StartNew();
		var completion = await _chat.CompleteChatAsync(messages); // NOTE: result is ClientResult<ChatCompletion>
		swAi.Stop();

		var draft = string.Concat(completion.Value.Content.Select(p => p.Text)); // NOTE: completion.Value.Content
		_log.LogInformation("ai.response latency_ms={ms} output_chars={chars}\n{preview}",
			 swAi.ElapsedMilliseconds, draft.Length, ApplyAiLogPolicy(draft)); // LOG

		var approvalId = Guid.NewGuid().ToString("N");
		lock (_lock)
			_pending[approvalId] = new Pending(cmd, chan.Id, target.Id, draft, cmd.User.Id);
		_log.LogInformation("answer.draft.created approval_id={approvalId}", approvalId); // LOG

		var components = new ComponentBuilder()
			 .WithButton("Approve", $"approve:{approvalId}", ButtonStyle.Success)
			 .WithButton("Cancel", $"cancel:{approvalId}", ButtonStyle.Danger)
			 .Build();

		await cmd.ModifyOriginalResponseAsync(m =>
		{
			m.Content = draft.Length <= 2000 ? draft : draft[..1990] + " …"; // show full potential reply (clamped for Discord)
			m.Components = components;
		});

		swTotal.Stop();
		_log.LogInformation("answer.ready elapsed_ms={ms}", swTotal.ElapsedMilliseconds); // LOG
		Divider("/answer ready", ("approval_id", approvalId)); // LOG
	}

	private async Task OnButtonAsync(SocketMessageComponent component)
	{
		await component.DeferAsync(); // ack button; no extra messages

		var parts = component.Data.CustomId.Split(':', 2);
		if (parts.Length != 2)
			return;
		var action = parts[0];
		var id = parts[1];

		Pending? p;
		lock (_lock)
			_pending.TryGetValue(id, out p);
		if (p is null || component.User.Id != p.RequestedByUserId)
			return;

		using var scope = _log.BeginScope(new Dictionary<string, object>
		{
			["interaction"] = p.Slash.Id.ToString(),
			["guild"] = p.Slash.GuildId ?? 0UL,
			["channel"] = p.Slash.ChannelId ?? 0,
			["invoker"] = p.RequestedByUserId,
			["approval_id"] = id,
			["target_msg"] = p.TargetMessageId
		});

		Divider($"button {action}", ("approval_id", id)); // LOG
		_log.LogInformation("answer.button action={action}", action); // LOG

		try
		{
			if (action == "cancel")
			{
				await p.Slash.DeleteOriginalResponseAsync();
				lock (_lock)
					_pending.Remove(id);
				_log.LogInformation("answer.cancelled"); // LOG
				Divider("answer end (cancelled)");
				return;
			}

			if (action == "approve")
			{
				var posted = new List<ulong>();
				if (_client.GetChannel(p.ChannelId) is IMessageChannel chan)
				{
					foreach (var chunk in Util.Splitter.ChunkForDiscord(p.Draft))
					{
						var msg = await chan.SendMessageAsync(chunk, messageReference: new MessageReference(p.TargetMessageId));
						posted.Add(msg.Id);
					}
				}

				await p.Slash.DeleteOriginalResponseAsync();
				lock (_lock)
					_pending.Remove(id);

				_log.LogInformation("answer.approved posted_count={count} posted_ids={ids}",
					 posted.Count, string.Join(",", posted)); // LOG
				Divider("answer end (approved)");
			}
		}
		catch (Exception ex)
		{
			_log.LogError(ex, "answer.button error action={action}", action); // LOG
		}
	}

	// Build a compact, model-friendly excerpt list
	private static string BuildContextBlock(IReadOnlyList<IMessage> context, IMessage target)
	{
		static string One(IMessage m)
		{
			var author = m.Author is SocketGuildUser gu
				? (gu.DisplayName ?? gu.GlobalName ?? gu.Username)
				: (m.Author.GlobalName ?? m.Author.Username);

			var when = m.Timestamp.UtcDateTime.ToString("u");
			var content = string.IsNullOrWhiteSpace(m.Content) ? "<no text>" : m.Content;
			// trim very long lines (attachments/embeds are ignored here)
			if (content.Length > 1200)
				content = content[..1200] + " …";
			return $"[{when}] {author}: {content}";
		}

		var sb = new StringBuilder();
		foreach (var m in context.OrderBy(m => m.Timestamp))
		{
			var mark = m.Id == target.Id ? "  <<TARGET>>" : "";
			sb.AppendLine(One(m) + mark);
		}
		return sb.ToString();
	}

	private string BuildRagBlock(string contextBlock, out int ragHits)
	{
		ragHits = 0;
		if (!_llm.IsLoaded)
			return "";
		var last = contextBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries)
									  .LastOrDefault(l => l.Contains("<<TARGET>>")) ?? contextBlock;
		var query = last.Replace("<<TARGET>>", "");
		var hits = _llm.Search(query, k: 4);
		ragHits = hits.Count;
		if (ragHits == 0)
			return "";

		var sb = new StringBuilder();
		sb.AppendLine("Harmony reference hints (selected):");
		foreach (var h in hits)
		{
			sb.AppendLine($"- {h.Signature ?? h.Id}");
			if (!string.IsNullOrWhiteSpace(h.Summary))
				sb.AppendLine($"  {h.Summary}");
			if (!string.IsNullOrWhiteSpace(h.DocUrl))
				sb.AppendLine($"  [docs] {h.DocUrl}");
		}
		return sb.ToString();
	}

	// Fetch backwards to find the first author name match; then collect +/- window.
	private static async Task<(IMessage? target, List<IMessage> context)> FindTargetAndContextAsync(
		SocketTextChannel channel, string who, int before, int after)
	{
		var whoLower = who.ToLowerInvariant();
		IMessage? found = null;

		// Walk backwards in pages. Use GetMessagesAsync with Direction.Before to paginate. :contentReference[oaicite:8]{index=8}
		ulong? lastId = null;
		for (int page = 0; page < 50 && found is null; page++)
		{
			var batch = lastId is null
				? await channel.GetMessagesAsync(limit: 100).FlattenAsync()
				: await channel.GetMessagesAsync(lastId.Value, Direction.Before, 100).FlattenAsync();

			if (!batch.Any())
				break;
			foreach (var m in batch)
			{
				var gu = m.Author as SocketGuildUser;
				var names = new[]
				{
					gu?.DisplayName, m.Author.GlobalName, m.Author.Username
				}.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.ToLowerInvariant());

				if (names.Any(n => n.Contains(whoLower)))
				{
					found = m;
					break;
				}
			}
			lastId = batch.Last().Id; // continue back
		}

		if (found is null)
			return (null, new List<IMessage>());

		// Get context: some before and after around the found message
		var beforeMsgs = await channel.GetMessagesAsync(found.Id, Direction.Before, before).FlattenAsync();
		var afterMsgs = await channel.GetMessagesAsync(found.Id, Direction.After, after).FlattenAsync(); // may return fewer after. :contentReference[oaicite:9]{index=9}

		var ctx = beforeMsgs.ToList();
		ctx.Add(found);
		ctx.AddRange(afterMsgs);
		return (found, ctx);
	}
}