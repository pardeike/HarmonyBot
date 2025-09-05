using Discord.WebSocket;
using OpenAI.Chat;
using System.Text;
using HarmonyBot.RAG;
using HarmonyBot.Util;
using Discord;

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

		_client.Log += msg => { Console.WriteLine($"[{msg.Severity}] {msg.Source}: {msg.Message}"); return Task.CompletedTask; };

		_client.Ready += OnReadyAsync;
		_client.SlashCommandExecuted += OnSlashCommandEntrypointAsync;
		_client.ButtonExecuted += OnButtonAsync;

		// OpenAI client (official SDK)
		_chat = new ChatClient(_cfg.ChatModel, _cfg.OpenAIApiKey); // gpt-4o by default; configurable
																					  // Example use of ChatClient documented here. :contentReference[oaicite:3]{index=3}

		_llm = LlmPackIndex.TryLoad(_cfg.LlmPackDir);
	}

	private Task OnSlashCommandEntrypointAsync(SocketSlashCommand cmd)
	{
		_ = Task.Run(async () =>
		{
			try
			{ await OnSlashCommandAsync(cmd); }
			catch (Exception ex)
			{
				Console.WriteLine($"Failed to run command {cmd.CommandName}: {ex}");
				// try to inform the invoker if we can
				try
				{ await SafeErrorAsync(cmd, "Unexpected error. Check logs."); }
				catch { /* ignore */ }
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
		await cmd.DeferAsync(ephemeral: true); // single ephemeral message “slot”. :contentReference[oaicite:5]{index=5}

		if (!IsOwner(cmd))
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = "Owner‑only."); return; }

		var who = cmd.Data.Options.First().Value?.ToString()?.Trim();
		if (string.IsNullOrEmpty(who))
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = "Provide a name fragment."); return; }

		if (cmd.Channel is not SocketTextChannel chan)
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = "Run /answer in a server text channel."); return; }

		var (target, context) = await FindTargetAndContextAsync(chan, who, _cfg.ContextBefore, _cfg.ContextAfter);
		if (target is null)
		{ await cmd.ModifyOriginalResponseAsync(m => m.Content = $"No recent message found for “{who}”."); return; }

		var targetUser = (target.Author as SocketGuildUser);
		var targetName = targetUser?.DisplayName ?? target.Author.GlobalName ?? target.Author.Username;

		var contextBlock = BuildContextBlock(context, target);
		var ragHints = BuildRagBlock(contextBlock);
		var sys = await File.ReadAllTextAsync("Prompts/SystemPrompt.txt");

		var messages = new List<OpenAI.Chat.ChatMessage>
	 {
		  new SystemChatMessage(sys),
		  new SystemChatMessage(ragHints), // may be empty
        new UserChatMessage($$"""
            Channel excerpts (oldest → newest):
            {{contextBlock}}

            Target to answer: {{targetName}} (message id: {{target.Id}})
            Task: Write a concise, helpful reply addressing {{targetName}}'s post directly.
            """)
	 };

		// NOTE: OpenAI .NET returns ClientResult<ChatCompletion> here
		var completion = await _chat.CompleteChatAsync(messages); // official API. :contentReference[oaicite:6]{index=6}
		var draft = string.Concat(completion.Value.Content.Select(p => p.Text));

		// single ephemeral preview + buttons (no extra follow‑ups)
		var approvalId = Guid.NewGuid().ToString("N");
		lock (_lock)
			_pending[approvalId] = new Pending(cmd, chan.Id, target.Id, draft, cmd.User.Id);

		var components = new ComponentBuilder()
			 .WithButton("Approve", $"approve:{approvalId}", ButtonStyle.Success)
			 .WithButton("Cancel", $"cancel:{approvalId}", ButtonStyle.Danger)
			 .Build();

		await cmd.ModifyOriginalResponseAsync(m =>
		{
			m.Content = Clamp(draft);               // preview == potential reply
			m.Components = components;              // approval row
		});

		// No FollowupAsync calls here → exactly one ephemeral message on screen.
	}

	private async Task OnButtonAsync(SocketMessageComponent component)
	{
		// Acknowledge immediately; don't send any new messages. :contentReference[oaicite:7]{index=7}
		await component.DeferAsync(); // plain ack is enough (no new ephemeral)

		var parts = component.Data.CustomId.Split(':', 2);
		if (parts.Length != 2)
			return;

		var action = parts[0];
		var id = parts[1];

		Pending? p;
		lock (_lock)
			_pending.TryGetValue(id, out p);
		if (p is null || component.User.Id != p.RequestedByUserId)
		{
			// Silent: no more ephemerals. Just drop.
			return;
		}

		try
		{
			if (action == "cancel")
			{
				// Remove preview and exit
				await p.Slash.DeleteOriginalResponseAsync(); // delete the original ephemeral preview. :contentReference[oaicite:8]{index=8}
				lock (_lock)
					_pending.Remove(id);
				Console.WriteLine($"[BOT] Cancelled draft for message {p.TargetMessageId} by {component.User.Username}.");
				return;
			}

			if (action == "approve")
			{
				// Post reply (chunked) to original message
				if (_client.GetChannel(p.ChannelId) is IMessageChannel chan)
				{
					foreach (var chunk in Util.Splitter.ChunkForDiscord(p.Draft))
						await chan.SendMessageAsync(chunk, messageReference: new MessageReference(p.TargetMessageId));
				}

				// Remove the preview
				await p.Slash.DeleteOriginalResponseAsync(); // nukes the single ephemeral preview
				lock (_lock)
					_pending.Remove(id);

				Console.WriteLine($"[BOT] Approved and posted reply to message {p.TargetMessageId}.");
				return;
			}
		}
		catch (Exception ex)
		{
			// Log only; no user-notifying ephemerals
			Console.WriteLine($"[BOT] Button handling error: {ex}");
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

	private string BuildRagBlock(string contextBlock)
	{
		if (!_llm.IsLoaded)
			return ""; // no-op if pack not present
						  // Use the latest user utterance (the TARGET) for search; else fallback to all text
		var last = contextBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries)
								.LastOrDefault(l => l.Contains("<<TARGET>>")) ?? contextBlock;
		var query = last.Replace("<<TARGET>>", "");
		var hits = _llm.Search(query, k: 4);
		if (hits.Count == 0)
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