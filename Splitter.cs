namespace HarmonyBot;

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
			// try not to cut in the middle of code fences or lines
			var slice = text.AsSpan(i, len).ToString();
			var lastBreak = slice.LastIndexOf('\n');
			if (lastBreak >= 0)
			{ len = lastBreak + 1; slice = slice[..len]; }
			var fences = 0;
			for (var idx = slice.IndexOf("```", StringComparison.Ordinal); idx >= 0; idx = slice.IndexOf("```", idx + 3, StringComparison.Ordinal))
				fences++;
			if (fences % 2 != 0)
			{
				var fenceBreak = slice.LastIndexOf("```", StringComparison.Ordinal);
				var fenceLine = slice.LastIndexOf('\n', fenceBreak - 1);
				if (fenceLine >= 0)
				{ len = fenceLine + 1; slice = slice[..len]; }
			}
			yield return slice;
			i += len;
		}
	}
}
