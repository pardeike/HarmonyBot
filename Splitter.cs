namespace HarmonyBot;

public static class Splitter
{
	public static IEnumerable<string> ChunkForDiscord(string text, int max = 2000)
	{
		if (string.IsNullOrEmpty(text))
			yield break;
		var i = 0;
		while (i < text.Length)
		{
			var remaining = text.Length - i;
			var len = Math.Min(max, remaining);
			var slice = text.AsSpan(i, len).ToString();

			if (remaining > max)
			{
				// try not to cut in the middle of code fences or lines
				var lastBreak = slice.LastIndexOf('\n');
				if (lastBreak > 0)
				{ len = lastBreak + 1; slice = slice[..len]; }
				var fences = 0;
				for (var idx = slice.IndexOf("```", StringComparison.Ordinal); idx >= 0; idx = slice.IndexOf("```", idx + 3, StringComparison.Ordinal))
					fences++;
				if (fences % 2 != 0)
				{
					var fenceBreak = slice.LastIndexOf("```", StringComparison.Ordinal);
					var fenceLine = slice.LastIndexOf('\n', fenceBreak - 1);
					if (fenceLine > 0)
					{ len = fenceLine + 1; slice = slice[..len]; }
				}
			}
			yield return slice;
			i += len;
		}
	}
}
