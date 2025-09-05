namespace HarmonyBot.Util;

public static class Splitter
{
	public static IEnumerable<string> ChunkForDiscord(string text, int max = 1900)
	{
		if (string.IsNullOrEmpty(text))
			yield break;
		var i = 0;
		while (i < text.Length)
		{
			var len = Math.Min(max, text.Length - i);
			// try not to cut in the middle of code fences/lines
			var slice = text.AsSpan(i, len).ToString();
			var lastBreak = slice.LastIndexOf('\n');
			if (lastBreak > 200)
			{ slice = slice[..lastBreak]; len = lastBreak; }
			yield return slice;
			i += len;
		}
	}
}