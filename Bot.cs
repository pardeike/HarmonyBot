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
	private const int TargetAnswerMaxChars = 1400;
	private const int DiscordMessageMaxChars = 2000;

	private readonly Config _cfg;
	private readonly DiscordSocketClient _client;
	private readonly ResponsesClient _chat;
	private readonly HttpClient _httpClient;
	private readonly LlmPackIndex _llm = new([]);

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


		_logAiContent = cfg.LogAiContent;
		_logAiContentMax = cfg.LogAiContentMax;

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
		_chat = new ResponsesClient(_cfg.OpenAIApiKey);

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

		// Build surrounding discussion plus the target author's forward burst.
		var context = await CollectContextAsync(chan, anchor, _cfg);

		var targetUser = anchor.Author as SocketGuildUser;
		var targetName = targetUser?.DisplayName ?? anchor.Author.GlobalName ?? anchor.Author.Username;

		var contextBlock = await BuildContextBlockAsync(_log, context, anchor, GetAttachmentTextAsync);
		_log.LogInformation("context.collected messages={messages} chars={chars} around_before={before} around_after={after}",
			context.Count, contextBlock.Length, _cfg.CtxPrependBefore, _cfg.CtxAppendAfter);
		var ragHints = BuildRagBlock(contextBlock, out var ragHintCount);
		var sys = await LoadSystemPromptAsync();

		var browsingInstructions = _cfg.WebSearchEnabled
			? "\nYou have live web access through the web_search tool. For Harmony-specific questions, use it when the Discord excerpts "
				+ "and selected reference cards do not contain enough concrete API names, version behavior, examples, or source evidence. "
				+ "Prioritize https://harmony.pardeike.net and https://github.com/pardeike/Harmony; use other sources only when they directly clarify the issue."
			: "";
		var instructions = ragHintCount > 0
			? $"{sys}\n{ragHints}{browsingInstructions}"
			: $"{sys}{browsingInstructions}";
		var userPrompt = $"Channel excerpts (oldest -> newest):{contextBlock}\nTask: Write a max {TargetAnswerMaxChars} character long, helpful reply directly addressing "
			+ $"{targetName}'s message with id {anchor.Id} and related messages. Be specific to Harmony and the user's code/problem; "
			+ "do not give generic troubleshooting unless the missing context makes that unavoidable.";
		var promptText = instructions + "\n\n" + userPrompt;
		_log.LogInformation("ai.request model={model} reasoning_effort={effort} web_search={webSearch} prompt_chars={chars} rag_hits={hits}\n{preview}",
			_cfg.ChatModel, _cfg.ReasoningEffort, _cfg.WebSearchEnabled, promptText.Length, ragHintCount, ApplyAiLogPolicy(promptText));

		var opts = new CreateResponseOptions
		{
			Model = _cfg.ChatModel,
			Instructions = instructions,
			ReasoningOptions = new ResponseReasoningOptions
			{
				ReasoningEffortLevel = ParseReasoningEffort(_cfg.ReasoningEffort)
			}
		};
		if (_cfg.WebSearchEnabled)
			opts.Tools.Add(ResponseTool.CreateWebSearchTool());
		opts.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));

		var swAi = Stopwatch.StartNew();
		var completion = await _chat.CreateResponseAsync(opts);
		swAi.Stop();

		var rawDraft = string.Concat(completion.Value.OutputItems
			.OfType<MessageResponseItem>()
			.SelectMany(i => i.Content.Select(p => p.Text))).Trim();
		var draft = DiscordMessageSizeClamp(rawDraft);
		_log.LogInformation("ai.response latency_ms={ms} output_chars={chars} draft_chars={draftChars}\n{preview}",
			swAi.ElapsedMilliseconds, rawDraft.Length, draft.Length, ApplyAiLogPolicy(draft));

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
			m.Content = DiscordMessageSizeClamp(draft); // preview == potential reply
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
					foreach (var chunk in Splitter.ChunkForDiscord(p.Draft, DiscordMessageMaxChars))
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

	// ---------- Grouping / Context collection ----------

	private static async Task<List<IMessage>> CollectContextAsync(SocketTextChannel channel, SocketMessage anchor, Config cfg)
	{
		var messages = new Dictionary<ulong, IMessage>();
		if (cfg.CtxPrependBefore > 0)
		{
			var before = await channel.GetMessagesAsync(anchor.Id, Direction.Before, cfg.CtxPrependBefore).FlattenAsync();
			foreach (var message in before)
				AddIfNearAnchor(messages, message, anchor, cfg);
		}
		messages[anchor.Id] = anchor;

		if (cfg.CtxAppendAfter > 0)
		{
			var after = await channel.GetMessagesAsync(anchor.Id, Direction.After, cfg.CtxAppendAfter).FlattenAsync();
			foreach (var message in after)
				AddIfNearAnchor(messages, message, anchor, cfg);
		}

		await CollectTargetAuthorForwardBurstAsync(channel, anchor, cfg, message => AddIfNearAnchor(messages, message, anchor, cfg));

		return LimitContext(messages.Values, anchor, cfg);
	}

	private static async Task CollectTargetAuthorForwardBurstAsync(
		 SocketTextChannel channel, SocketMessage anchor, Config cfg, Action<IMessage> addMessage)
	{
		var lastAuthorTime = anchor.Timestamp;
		ulong? cursor = anchor.Id;
		var interposts = 0;

		while (true)
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

				if (m.Author.Id == anchor.Author.Id)
				{
					var gap = (m.Timestamp - lastAuthorTime).TotalSeconds;
					if (gap > cfg.GroupMaxGapSec)
					{ cursor = null; break; } // next burst → stop
					addMessage(m);
					lastAuthorTime = m.Timestamp;
					interposts = 0;
				}
				else
				{
					if (!cfg.IncludeInterposts)
						continue; // skip other authors when interposts are excluded

					interposts++;
					if (interposts > cfg.GroupMaxInterposts)
					{ cursor = null; break; }
					addMessage(m); // keep limited interposts for context
				}
			}

			if (cursor is null)
				break;
			cursor = last!.Id;
		}
	}

	private static void AddIfNearAnchor(Dictionary<ulong, IMessage> messages, IMessage message, SocketMessage anchor, Config cfg)
	{
		if (message.Id == anchor.Id)
		{
			messages[message.Id] = message;
			return;
		}

		if (Math.Abs((message.Timestamp - anchor.Timestamp).TotalSeconds) > cfg.GroupMaxDurationSec)
			return;

		messages.TryAdd(message.Id, message);
	}

	private static List<IMessage> LimitContext(IEnumerable<IMessage> messages, SocketMessage anchor, Config cfg)
	{
		static int MessageLength(IMessage msg) =>
				  (msg.Content?.Length ?? 0) + msg.Attachments.Sum(a => a.Description?.Length ?? 0);

		var result = new List<IMessage>();
		var totalChars = 0;
		foreach (var message in messages.OrderBy(m => m.Timestamp).ThenBy(m => m.Id))
		{
			var length = MessageLength(message);
			var isAnchor = message.Id == anchor.Id;
			if (!isAnchor && (result.Count >= cfg.CtxMaxMessages || totalChars + length > cfg.CtxMaxChars))
				continue;

			result.Add(message);
			totalChars += length;
		}

		if (!result.Any(message => message.Id == anchor.Id))
			result.Add(anchor);
		return result.OrderBy(m => m.Timestamp).ThenBy(m => m.Id).ToList();
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
		if (attachment.Size > _cfg.MaxFileSize)
			return $"[{attachment.Filename}: file too large ({attachment.Size} > {_cfg.MaxFileSize} bytes)]";

		try
		{
			using var response = await _httpClient.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead);
			if (!response.IsSuccessStatusCode)
				return $"[{attachment.Filename}: download failed]";

			var content = await response.Content.ReadAsStringAsync();
			if (content.Length > _cfg.MaxFileSize)
				content = content[.._cfg.MaxFileSize] + " …";

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
		var query = BuildRagQuery(contextBlock);
		var hits = _llm.Search(query, k: _cfg.MaxCardCount);
		ragHits = hits.Count;
		if (ragHits == 0)
			return "";

		_log.LogInformation("Using {cardCount} cards as context", ragHits);

		var sb = new StringBuilder().AppendLine("Harmony reference hints (selected):");
		foreach (var h in hits.Take(_cfg.MaxCardCount))
		{
			_ = sb.AppendLine($"- {h.Signature ?? h.Id}");
			if (!string.IsNullOrWhiteSpace(h.Summary))
				_ = sb.AppendLine($"  {h.Summary}");
			if (!string.IsNullOrWhiteSpace(h.Remarks))
				_ = sb.AppendLine($"  {SingleLineClamp(h.Remarks, 360)}");
			var example = h.Examples?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Code));
			if (example is not null)
				_ = sb.AppendLine($"  [example] {SingleLineClamp(example.Code!, 420)}");
			if (!string.IsNullOrWhiteSpace(h.DocUrl))
				_ = sb.AppendLine($"  [docs] {h.DocUrl}");
		}
		return sb.ToString();
	}

	private static string BuildRagQuery(string contextBlock)
	{
		var lines = contextBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		var target = lines.LastOrDefault(l => l.Contains(" <<TARGET>>", StringComparison.Ordinal))?.Replace(" <<TARGET>>", "") ?? contextBlock;
		var sb = new StringBuilder()
			.AppendLine(target)
			.AppendLine(target);

		foreach (var line in lines.Where(IsHarmonySignalLine).Take(20))
			_ = sb.AppendLine(line.Replace(" <<TARGET>>", ""));

		return sb.ToString();
	}

	private static bool IsHarmonySignalLine(string line)
	{
		string[] signals =
		[
			"Harmony", "HarmonyPatch", "AccessTools", "TargetMethod", "Prepare", "Cleanup", "Prefix", "Postfix", "Transpiler",
			"Finalizer", "ReversePatch", "PatchAll", "__instance", "__result", "__state", "___", "CodeInstruction",
			"MethodInfo", "ConstructorInfo", "BindingFlags", "Traverse", "priority", "before", "after", "generic", "iterator", "async"
		];
		return signals.Any(signal => line.Contains(signal, StringComparison.OrdinalIgnoreCase));
	}

	private static string SingleLineClamp(string text, int max)
	{
		var line = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
		return line.Length <= max ? line : line[..(max - 2)] + " …";
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

	private static ResponseReasoningEffortLevel ParseReasoningEffort(string effort) => effort.Trim().ToLowerInvariant() switch
	{
		"none" => ResponseReasoningEffortLevel.None,
		"minimal" => ResponseReasoningEffortLevel.Minimal,
		"low" => ResponseReasoningEffortLevel.Low,
		"medium" => ResponseReasoningEffortLevel.Medium,
		"high" => ResponseReasoningEffortLevel.High,
		"xhigh" or "extra-high" or "extra_high" => new ResponseReasoningEffortLevel("xhigh"),
		_ => ResponseReasoningEffortLevel.High
	};

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


	private static string DiscordMessageSizeClamp(string s, int max = DiscordMessageMaxChars)
	{
		if (s.Length <= max)
			return s;

		const string suffix = " …";
		var limit = max - suffix.Length;
		var slice = s[..limit];
		var cut = FindLastNaturalBreak(slice, limit);
		var result = slice[..cut].TrimEnd() + suffix;

		if (CountCodeFences(result) % 2 == 0)
			return result;

		const string closeFence = "\n```";
		var closeFenceLimit = max - closeFence.Length;
		if (result.Length > closeFenceLimit)
			result = result[..closeFenceLimit].TrimEnd();
		return result + closeFence;
	}

	private static int FindLastNaturalBreak(string slice, int fallback)
	{
		var minimum = (int)(fallback * 0.65);
		var paragraphBreak = slice.LastIndexOf("\n\n", StringComparison.Ordinal);
		if (paragraphBreak >= minimum)
			return paragraphBreak;

		var lineBreak = slice.LastIndexOf('\n');
		if (lineBreak >= minimum)
			return lineBreak;

		var sentenceBreak = Math.Max(slice.LastIndexOf('.'), Math.Max(slice.LastIndexOf('!'), slice.LastIndexOf('?')));
		return sentenceBreak >= minimum ? sentenceBreak + 1 : fallback;
	}

	private static int CountCodeFences(string text)
	{
		var fences = 0;
		for (var idx = text.IndexOf("```", StringComparison.Ordinal); idx >= 0; idx = text.IndexOf("```", idx + 3, StringComparison.Ordinal))
			fences++;
		return fences;
	}

	public void Dispose()
	{
		(_chat as IDisposable)?.Dispose();
		_loggerFactory?.Dispose();
		_httpClient?.Dispose();
		_client?.Dispose();
	}
}
#pragma warning restore OPENAI001
