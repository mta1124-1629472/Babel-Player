namespace PlayerApp.Core
{
    public static class ModelManager
    {
        private static readonly string ModelsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlayerApp",
            "models");

        public static Task<string> EnsureModelForLanguageAsync(string languageCode)
        {
            var normalized = string.IsNullOrWhiteSpace(languageCode) ? "und" : languageCode.Trim().ToLowerInvariant();
            Directory.CreateDirectory(ModelsRoot);

            var modelPath = Path.Combine(ModelsRoot, $"mt_{normalized}_en.onnx");
            return Task.FromResult(modelPath);
        }

        public static string GetAsrModelsDirectory()
        {
            var asrRoot = Path.Combine(ModelsRoot, "asr");
            Directory.CreateDirectory(asrRoot);
            return asrRoot;
        }
    }
}
