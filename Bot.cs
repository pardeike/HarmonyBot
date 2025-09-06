using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.Diagnostics;
using System.Text;

#pragma warning disable OPENAI001
namespace HarmonyBot;

public sealed class Bot : IDisposable
{
	private readonly Config _cfg;
	private readonly DiscordSocketClient _client;
	private readonly OpenAIResponseClient _chat;
	private readonly HttpClient _httpClient;
	private LlmPackIndex _llm = new([]);

	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger _log;

	private readonly string _logAiContent;
	private readonly int _logAiContentMax;

	private readonly Dictionary<string, Pending> _pending = [];
	private readonly object _lock = new();

	private sealed record Pending(
		 SocketInteraction Interaction,   // original interaction (message command)
		 ulong ChannelId,                 // channel where we will post
		 ulong TargetMessageId,           // anchor message id to reply to
		 string Draft,                    // potential reply
		 ulong RequestedByUserId          // approver/canceller
	);

	public Bot(Config cfg)
	{
		_cfg = cfg;

		_loggerFactory = LogSetup.CreateLoggerFactory();
		_log = _loggerFactory.CreateLogger<Bot>();

		_logAiContent = (Environment.GetEnvironmentVariable("LOG_AI_CONTENT") ?? "truncated").ToLowerInvariant();
		_logAiContentMax = int.TryParse(Environment.GetEnvironmentVariable("LOG_AI_CONTENT_MAX"), out var n) ? n : 4000;

		_log.LogInformation("Configuration: {configuration}", _cfg.Summary);

		_client = new DiscordSocketClient(new DiscordSocketConfig
		{
			GatewayIntents =
				  GatewayIntents.Guilds |
				  GatewayIntents.GuildMessages |
				  GatewayIntents.MessageContent,   // must be enabled in Dev Portal
			AlwaysDownloadUsers = false,
			LogGatewayIntentWarnings = false
		});

		_client.Log += msg =>
		{
			_log.Log(MapLevel(msg.Severity), "[{Source}] {Message}", msg.Source, msg.Message);
			if (msg.Exception is not null)
				_log.LogError(msg.Exception, "Discord exception ({Source})", msg.Source);
			return Task.CompletedTask;
		};

		_client.Ready += OnReadyAsync;

		// Message Context Command entrypoint (right‑click on a message → Apps)
		_client.MessageCommandExecuted += OnMessageCommandEntrypointAsync;

		// Approve/Cancel buttons
		_client.ButtonExecuted += OnButtonAsync;

		// OpenAI client
		_chat = new OpenAIResponseClient(_cfg.ChatModel, _cfg.OpenAIApiKey);

		// HTTP client for downloading attachments
		_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

		// Optional Harmony reference pack
		_llm = LlmPackIndex.LoadAsync(_cfg, _log).Result;
	}

	public async Task RunAsync()
	{
		await _client.LoginAsync(TokenType.Bot, _cfg.DiscordToken);
		await _client.StartAsync();
		await Task.Delay(-1);
	}

	// ---------- Ready: register Message Context Command ----------

	private async Task OnReadyAsync()
	{
		// Register per guild for instant availability
		var msgCmd = new MessageCommandBuilder()
			 .WithName("Answer from here")
			 .Build();

		foreach (var g in _client.Guilds)
		{
			try
			{
				_ = await _client.Rest.CreateGuildCommand(msgCmd, g.Id);
			}
			catch (Exception ex)
			{
				_log.LogError(ex, "Failed to create message command in {Guild}", g.Name);
			}
		}

		_log.LogInformation("Ready");
	}

	// ---------- Entry wrapper to avoid blocking the gateway ----------

	private Task OnMessageCommandEntrypointAsync(SocketMessageCommand cmd)
	{
		_ = Task.Run(async () =>
		{
			try
			{ await OnMessageCommandAsync(cmd); }
			catch (Exception ex)
			{
				_log.LogError(ex, "Unhandled exception in message command {Command}", cmd.CommandName);
				try
				{ await cmd.RespondAsync("Unexpected error. Check logs.", ephemeral: true); }
				catch { /* ignore */ }
			}
		});
		return Task.CompletedTask;
	}

	// ---------- Message Context Command handler ----------

	private async Task OnMessageCommandAsync(SocketMessageCommand cmd)
	{
		await cmd.DeferAsync(ephemeral: true); // acknowledge; single ephemeral preview “slot”

		var anchor = cmd.Data.Message; // IMessage
		if (anchor is null)
		{
			_ = await cmd.ModifyOriginalResponseAsync(m => m.Content = "No message payload.");
			_log.LogWarning("message-cmd.bad_request no-anchor");
			return;
		}
		if (anchor.Channel is not SocketTextChannel chan)
		{
			_ = await cmd.ModifyOriginalResponseAsync(m => m.Content = "Use inside a server text channel.");
			_log.LogWarning("message-cmd.bad_request not-text-channel");
			return;
		}

		using var scope = _log.BeginScope(new Dictionary<string, object>
		{
			["interaction"] = cmd.Id.ToString(),
			["guild"] = cmd.GuildId ?? 0UL,
			["channel"] = cmd.ChannelId ?? 0,
			["invoker"] = cmd.User.Id,
			["anchor_msg"] = anchor.Id,
			["anchor_author"] = anchor.Author.Id
		});

		Divider("message-cmd start", ("anchor", anchor.Id), ("author", anchor.Author.Username));

		// Build logical group FORWARD from the anchor; also prepend a few messages before the anchor
		var context = await CollectGroupForwardAsync(chan, anchor, anchor.Author.Id, _cfg);

		var targetUser = anchor.Author as SocketGuildUser;
		var targetName = targetUser?.DisplayName ?? anchor.Author.GlobalName ?? anchor.Author.Username;

		var contextBlock = await BuildContextBlockAsync(_log, context, anchor, GetAttachmentTextAsync);
		var ragHints = BuildRagBlock(contextBlock, out var ragHintCount);
		var sys = await LoadSystemPromptAsync();

		var instructions = ragHintCount > 0
			? $"{sys}\n{ragHints}"
			: $"{sys}\nYou may browse https://harmony.pardeike.net and https://github.com/pardeike/Harmony for additional context.";
		var userPrompt = $"Channel excerpts (oldest -> newest):{contextBlock}\nTask: Write a max 1400 character long, helpful reply directly addressing {targetName}'s message with id {anchor.Id} and related messages.";
		var promptText = instructions + "\n\n" + userPrompt;
		_log.LogInformation("ai.request model={model} prompt_chars={chars} rag_hits={hits}\n{preview}",
			_cfg.ChatModel, promptText.Length, ragHintCount, ApplyAiLogPolicy(promptText));

		var opts = new ResponseCreationOptions { Instructions = instructions };
		if (ragHintCount == 0)
			opts.Tools.Add(ResponseTool.CreateWebSearchTool());

		var swAi = Stopwatch.StartNew();
		var completion = await _chat.CreateResponseAsync(userPrompt, opts);
		swAi.Stop();

		var draft = string.Concat(completion.Value.OutputItems
			.OfType<MessageResponseItem>()
			.SelectMany(i => i.Content.Select(p => p.Text)));
		_log.LogInformation("ai.response latency_ms={ms} output_chars={chars}\n{preview}",
			swAi.ElapsedMilliseconds, draft.Length, ApplyAiLogPolicy(draft));

		var approvalId = Guid.NewGuid().ToString("N");
		lock (_lock)
			_pending[approvalId] = new Pending(cmd, chan.Id, anchor.Id, draft, cmd.User.Id);
		_log.LogInformation("answer.draft.created approval_id={approvalId}", approvalId);

		var components = new ComponentBuilder()
			 .WithButton("Approve", $"approve:{approvalId}", ButtonStyle.Success)
			 .WithButton("Cancel", $"cancel:{approvalId}", ButtonStyle.Danger)
			 .Build();

		_ = await cmd.ModifyOriginalResponseAsync(m =>
		{
			m.Content = Clamp(draft); // preview == potential reply; clamped to Discord limit
			m.Components = components;
		});

		Divider("message-cmd ready", ("approval_id", approvalId));
	}

	// ---------- Buttons: Approve / Cancel ----------

	private async Task OnButtonAsync(SocketMessageComponent component)
	{
		await component.DeferAsync(ephemeral: true); // ack the click; no extra messages

		var parts = component.Data.CustomId.Split(':', 2);
		if (parts.Length != 2)
			return;
		var action = parts[0];
		var id = parts[1];

		Pending? p;
		lock (_lock)
			_ = _pending.TryGetValue(id, out p);
		if (p is null || component.User.Id != p.RequestedByUserId)
			return;

		using var scope = _log.BeginScope(new Dictionary<string, object>
		{
			["interaction"] = p.Interaction.Id.ToString(),
			["guild"] = (p.Interaction as SocketCommandBase)?.GuildId ?? 0UL,
			["channel"] = (p.Interaction as SocketCommandBase)?.ChannelId ?? 0UL,
			["invoker"] = p.RequestedByUserId,
			["approval_id"] = id,
			["target_msg"] = p.TargetMessageId
		});

		Divider($"button {action}", ("approval_id", id));
		_log.LogInformation("answer.button action={action}", action);

		try
		{
			if (action == "cancel")
			{
				try
				{ await p.Interaction.DeleteOriginalResponseAsync(); }
				catch { /* ignore */ }
				lock (_lock)
					_ = _pending.Remove(id);
				_log.LogInformation("answer.cancelled");
				Divider("answer end (cancelled)");
				return;
			}

			if (action == "approve")
			{
				var posted = new List<ulong>();
				if (_client.GetChannel(p.ChannelId) is IMessageChannel chan)
				{
					foreach (var chunk in Splitter.ChunkForDiscord(p.Draft))
					{
						var msg = await chan.SendMessageAsync(chunk, messageReference: new MessageReference(p.TargetMessageId));
						posted.Add(msg.Id);
					}
				}

				try
				{ await p.Interaction.DeleteOriginalResponseAsync(); }
				catch { /* ignore */ }
				lock (_lock)
					_ = _pending.Remove(id);

				_log.LogInformation("answer.approved posted_count={count} posted_ids={ids}", posted.Count, string.Join(",", posted));
				Divider("answer end (approved)");
			}
		}
		catch (Exception ex)
		{
			_log.LogError(ex, "answer.button error action={action}", action);
		}
	}

	// ---------- Grouping / Context collection (forward from anchor) ----------

	private static async Task<List<IMessage>> CollectGroupForwardAsync(
		 SocketTextChannel channel, SocketMessage anchor, ulong authorId, Config cfg)
	{
		var list = new List<IMessage>();

		// Prepend some context before anchor (chronological)
		var before = await channel.GetMessagesAsync(anchor.Id, Direction.Before, cfg.CtxPrependBefore).FlattenAsync();
		list.AddRange(before.Reverse());
		list.Add(anchor);

		var lastAuthorTime = anchor.Timestamp;
		ulong? cursor = anchor.Id;
		var interposts = 0;

		while (list.Count < cfg.CtxMaxMessages)
		{
			var page = (await channel.GetMessagesAsync(cursor!.Value, Direction.After, 100).FlattenAsync())
					  .OrderBy(m => m.Timestamp)
					  .ThenBy(m => m.Id)
					  .ToList();
			if (page.Count == 0)
				break;

			IMessage? last = null;
			foreach (var m in page)
			{
				last = m;
				// hard cap on total span from the anchor
				if ((m.Timestamp - anchor.Timestamp).TotalSeconds > cfg.GroupMaxDurationSec)
				{
					cursor = null;
					break;
				}

				if (m.Author.Id == authorId)
				{
					var gap = (m.Timestamp - lastAuthorTime).TotalSeconds;
					if (gap > cfg.GroupMaxGapSec)
					{ cursor = null; break; } // next burst → stop
					list.Add(m);
					lastAuthorTime = m.Timestamp;
					interposts = 0;
				}
				else
				{
					if (!cfg.IncludeInterposts)
					{ cursor = null; break; }
					interposts++;
					if (interposts > cfg.GroupMaxInterposts)
					{ cursor = null; break; }
					list.Add(m); // keep limited interposts for context
				}

				// character budget to avoid over-long prompts
				var chars = list.Sum(x => (x.Content?.Length ?? 0));
				if (chars >= cfg.CtxMaxChars)
				{ cursor = null; break; }
			}

			if (cursor is null)
				break;
			cursor = last!.Id;
		}

		return list;
	}

	// ---------- Prompt building helpers ----------

	private async Task<string> GetAttachmentTextAsync(IAttachment attachment)
	{
		_log.LogInformation("attachment {name} of type [{type}] and size {size}", attachment.Filename, attachment.ContentType, attachment.Size);

		// Only process text files
		if (string.IsNullOrWhiteSpace(attachment.ContentType) ||
			!attachment.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
			return "";

		// Limit file size to avoid huge downloads
		if (attachment.Size > 50_000) // 50KB limit
			return $"[{attachment.Filename}: file too large ({attachment.Size} bytes)]";

		try
		{
			var response = await _httpClient.GetAsync(attachment.Url);
			if (!response.IsSuccessStatusCode)
				return $"[{attachment.Filename}: download failed]";

			var content = await response.Content.ReadAsStringAsync();
			if (content.Length > 10_000) // 10KB text limit
				content = content[..10_000] + " …";

			return $"[Attachment: {attachment.Filename}]\n{content}\n[End of {attachment.Filename}]";
		}
		catch (Exception ex)
		{
			_log.LogWarning(ex, "Failed to download text attachment {Filename} from {Url}",
				attachment.Filename, attachment.Url);
			return $"[{attachment.Filename}: download error]";
		}
	}

	private static async Task<string> BuildContextBlockAsync(ILogger log, IReadOnlyList<IMessage> context, SocketMessage target, Func<IAttachment, Task<string>> getAttachmentText)
	{
		static async Task<string> OneAsync(ILogger log, IMessage m, ulong messageId, Func<IAttachment, Task<string>> getAttachmentText)
		{
			var author = m.Author is SocketGuildUser gu ? (gu.DisplayName ?? gu.GlobalName ?? gu.Username) : (m.Author.GlobalName ?? m.Author.Username);
			var when = m.Timestamp.UtcDateTime.ToString("u");
			var content = string.IsNullOrWhiteSpace(m.Content) ? "<no text>" : m.Content;
			if (content.Length > 12000)
				content = content[..12000] + " …";

			// Process text attachments
			var attachmentTexts = new List<string>();
			foreach (var attachment in m.Attachments)
			{
				var attachmentText = await getAttachmentText(attachment);
				if (!string.IsNullOrWhiteSpace(attachmentText))
					attachmentTexts.Add(attachmentText);
			}

			var fullContent = content;
			if (attachmentTexts.Count > 0)
				fullContent += "\n" + string.Join("\n", attachmentTexts);

			return $"- On {when}, {author} wrote message id {messageId}: {fullContent}";
		}

		var sb = new StringBuilder();
		foreach (var m in context.OrderBy(m => m.Timestamp))
		{
			var mark = m.Id == target.Id ? " <<TARGET>>" : "";
			var messageText = await OneAsync(log, m, m.Id, getAttachmentText);
			_ = sb.AppendLine(messageText + mark);
		}
		return sb.ToString();
	}

	private string BuildRagBlock(string contextBlock, out int ragHits)
	{
		ragHits = 0;
		if (!_llm.IsLoaded)
			return "";
		var last = contextBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries)
									  .LastOrDefault(l => l.Contains(" <<TARGET>>")) ?? contextBlock;
		var query = last.Replace(" <<TARGET>>", "");
		var hits = _llm.Search(query, k: _cfg.MaxCardCount);
		ragHits = hits.Count;
		if (ragHits == 0)
			return "";

		_log.LogInformation("Using {cardCount} cards as context", ragHits);

		var sb = new StringBuilder().AppendLine("Harmony reference hints (selected):");
		foreach (var h in hits)
		{
			_ = sb.AppendLine($"- {h.Signature ?? h.Id}");
			if (!string.IsNullOrWhiteSpace(h.Summary))
				_ = sb.AppendLine($"  {h.Summary}");
			if (!string.IsNullOrWhiteSpace(h.DocUrl))
				_ = sb.AppendLine($"  [docs] {h.DocUrl}");
		}
		return sb.ToString();
	}

	// ---------- Logging helpers ----------

	private static async Task<string> LoadSystemPromptAsync()
	{
		try
		{
			return await File.ReadAllTextAsync("Prompts/SystemPrompt.txt");
		}
		catch
		{
			// fallback minimal system prompt
			return "You are a concise, pragmatic Harmony support assistant. Prefer short, correct answers grounded in the provided excerpts.";
		}
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

	private void Divider(string title, params (string Key, object? Val)[] kv)
	{
		var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'");
		var meta = kv is { Length: > 0 } ? " // " + string.Join(", ", kv.Select(p => $"{p.Key}={p.Val}")) : "";
		_log.LogInformation("========== {Timestamp} :: {Title}{Meta} ==========", ts, title, meta);
	}

	private string ApplyAiLogPolicy(string s) => _logAiContent switch
	{
		"none" => "[suppressed]",
		"full" => s,
		_ => s.Length <= _logAiContentMax ? s : s[.._logAiContentMax] + " …"
	};


	private static string Clamp(string s, int max = 2000)
				=> s.Length <= max ? s : s[..(max - 2)] + " …";

	public void Dispose()
	{
		_httpClient?.Dispose();
		_client?.Dispose();
	}
}
#pragma warning restore OPENAI001
