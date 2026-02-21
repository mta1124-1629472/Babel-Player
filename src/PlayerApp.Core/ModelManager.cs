namespace PlayerApp.Core
{
	public static class ModelManager
	{
		public static async Task<string> EnsureModelForLanguageAsync(string languageCode)
		{
			// Check model_manifest.json and local model store.
			// If model missing, prompt user and download (not implemented here).
			// Return local model path.
			await Task.CompletedTask;
			return $"%LOCALAPPDATA%/PlayerApp/models/mt_{languageCode}_en.onnx";
		}
	}
}
