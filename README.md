# HarmonyBot

## About

The Discord bot for the official discord support server for the official Lib.Harmony open source project.

## Logging

Two environment variables control how much of the LLM prompt and response are written to the logs:

- `LOG_AI_CONTENT` (default: `full`): `none` suppresses content, `truncated` limits output, and `full` mirrors the API traffic.
- `LOG_AI_CONTENT_MAX` (default: `4000`): maximum characters when `LOG_AI_CONTENT` is `truncated`.
