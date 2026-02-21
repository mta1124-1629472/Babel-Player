using System.Text;

namespace PlayerApp.Core
{
	public class SubtitleManager
	{
		private List<(TranscriptChunk chunk, string english)> _finalized = new();
		private object _overlay; // Keep as object - no WinUI ref needed

		public SubtitleManager(object overlay)
		{
			_overlay = overlay;
		}

		public void ShowInterim(TranscriptChunk chunk)
		{
			// UI rendering happens in MainWindow.xaml.cs, not here
		}

		public void CommitFinal(TranscriptChunk chunk, string english)
		{
			_finalized.Add((chunk, english));
		}

		public void ExportSrt(string path)
		{
			var sb = new StringBuilder();
			int idx = 1;
			foreach (var (chunk, text) in _finalized)
			{
				sb.AppendLine(idx.ToString());
				sb.AppendLine($"{FormatTime(chunk.StartTimeSec)} --> {FormatTime(chunk.EndTimeSec)}");
				sb.AppendLine(text);
				sb.AppendLine();
				idx++;
			}
			File.WriteAllText(path, sb.ToString());
		}

		private string FormatTime(double seconds)
		{
			var ts = System.TimeSpan.FromSeconds(seconds);
			return ts.ToString(@"hh\:mm\:ss\,fff");
		}
	}
}
