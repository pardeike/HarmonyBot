# HarmonyBot

## About

The Discord bot for the official discord support server for the official Lib.Harmony open source project.

## Setup

1. Clone the repository.
2. Create a Discord bot and get its token from the [Discord Developer Portal](https://discord.com/developers/applications).
3. Create an OpenAI API key from the [OpenAI Platform](https://platform.openai.com/account/api-keys).
4. Create a file ~/.api-keys with the json format:
	```json
	{
	  "DISCORD_TOKEN": "YOUR_DISCORD_BOT_TOKEN",
	  "OPENAI_API_KEY": "YOUR_OPENAI_API_KEY"
	}
	```
5. Install .NET 9 SDK from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
6. Navigate to the project directory and run:
	```bash
	dotnet run
	```

## Environment Variables

- `API_KEYS_FILE` (default: `~/.api-keys`): the path to the API keys file
- `LOG_FORMAT` (default: `text`): the log format, either `text` or `json`.
- `LOG_LEVEL` (default: `Information`): the log level, either `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical`.
- `CHAT_MODEL` (default: `gpt-4o`): the OpenAI chat model to use.
- `LLM_PACK_URI` (default: `https://harmony.pardeike.net/llm-pack/harmony.cards.jsonl`): the URI of the LLM pack to use.
- `MAX_CARD_COUNT` (default: `10`): the maximum number of cards to use from the LLM pack.
- `MAX_FILE_SIZE` (default: `4_194_304`): the maximum file size for attachments.
- `GROUP_MAX_GAP_SEC` (default: `300`): the maximum gap between messages in a group.
- `GROUP_MAX_DURATION_SEC` (default: `1800`): the maximum duration of a group session.
- `GROUP_MAX_INTERPOSTS` (default: `6`): the maximum number of interposts in a group.
- `CTX_PREPEND_BEFORE` (default: `3`): the number of messages to prepend before the context.
- `CTX_MAX_MESSAGES` (default: `60`): the maximum number of messages in the context.
- `CTX_MAX_CHARS` (default: `100_000`): the maximum number of characters in the context.
- `CTX_INCLUDE_INTERPOSTS` (default: `true`): whether to include interposts in the context.
- `LOG_AI_CONTENT` (default: `full`): `none` suppresses content, `truncated` limits output, and `full` mirrors the API traffic.
- `LOG_AI_CONTENT_MAX` (default: `4000`): maximum characters when `LOG_AI_CONTENT` is `truncated`.
