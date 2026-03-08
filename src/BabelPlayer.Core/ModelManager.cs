using Whisper.net.Ggml;

namespace BabelPlayer.Core
{
    public static class ModelManager
    {
        private static readonly string ModelsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer",
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

        public static string GetAsrModelPath(GgmlType modelType)
        {
            return Path.Combine(GetAsrModelsDirectory(), $"ggml-{modelType.ToString().ToLowerInvariant()}-q5_0.bin");
        }

        public static bool HasAsrModel(GgmlType modelType)
        {
            return File.Exists(GetAsrModelPath(modelType));
        }
    }
}
