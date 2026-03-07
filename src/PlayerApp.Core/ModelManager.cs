namespace PlayerApp.Core
{
    public static class ModelManager
    {
        public static Task<string> EnsureModelForLanguageAsync(string languageCode)
        {
            var normalized = string.IsNullOrWhiteSpace(languageCode) ? "und" : languageCode.Trim().ToLowerInvariant();
            var modelsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlayerApp",
                "models");

            Directory.CreateDirectory(modelsRoot);

            var modelPath = Path.Combine(modelsRoot, $"mt_{normalized}_en.onnx");
            return Task.FromResult(modelPath);
        }
    }
}
