namespace PlayerApp.Core
{
	public static class HardwareDetector
	{
		public static string GetSummary()
		{
			var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? Environment.MachineName;
			var cores = Environment.ProcessorCount;
			var profile = cores >= 8 ? "High" : cores >= 4 ? "Balanced" : "Power Saver";
			return $"CPU: {cpu}; Cores: {cores}; Inference Profile: {profile}";
		}
	}
}
