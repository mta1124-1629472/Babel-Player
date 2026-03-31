using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;

namespace Babel.Player.Services;

public enum ProviderReadiness
{
    Ready,
    RequiresDownload,
    RequiresApiKey,
    Unsupported
}

/// <summary>
/// Pre-flight readiness check for providers and models.
/// Resolves whether a model is ready to execute, needs an API key, requires a local download,
/// or is not supported yet (fake-ready UI option).
/// </summary>
public static class ProviderReadinessResolver
{
    public static ProviderReadiness ResolveTranscription(string provider, string model, ApiKeyStore? keys)
    {
        try
        {
            ProviderCapability.ValidateTranscription(provider, model, keys);
        }
        catch (PipelineProviderException ex) when (IsApiKeyMissingException(ex))
        {
            return ProviderReadiness.RequiresApiKey;
        }
        catch (PipelineProviderException)
        {
            return ProviderReadiness.Unsupported;
        }

        if (provider == ProviderNames.FasterWhisper)
        {
            if (!IsFasterWhisperDownloaded(model))
                return ProviderReadiness.RequiresDownload;
        }

        return ProviderReadiness.Ready;
    }

    private static bool IsApiKeyMissingException(PipelineProviderException ex)
    {
        // Check for various patterns that indicate API key is missing
        var message = ex.Message;
        return message.Contains("API key for") || 
               message.Contains("would also be required") ||
               (message.Contains("not implemented yet") && message.Contains("API key"));
    }

    public static ProviderReadiness ResolveTranslation(string provider, string model, ApiKeyStore? keys)
    {
        try
        {
            ProviderCapability.ValidateTranslation(provider, model, keys);
        }
        catch (PipelineProviderException ex) when (IsApiKeyMissingException(ex))
        {
            return ProviderReadiness.RequiresApiKey;
        }
        catch (PipelineProviderException)
        {
            return ProviderReadiness.Unsupported;
        }

        if (provider == ProviderNames.Nllb200)
        {
            if (!IsNllbDownloaded(model))
                return ProviderReadiness.RequiresDownload;
        }

        return ProviderReadiness.Ready;
    }

    public static ProviderReadiness ResolveTts(string provider, string voice, string? piperModelDir, ApiKeyStore? keys)
    {
        try
        {
            ProviderCapability.ValidateTts(provider, voice, keys);
        }
        catch (PipelineProviderException ex) when (IsApiKeyMissingException(ex))
        {
            return ProviderReadiness.RequiresApiKey;
        }
        catch (PipelineProviderException)
        {
            return ProviderReadiness.Unsupported;
        }

        if (provider == ProviderNames.Piper)
        {
            if (!IsPiperVoiceDownloaded(voice, piperModelDir))
                return ProviderReadiness.RequiresDownload;
        }

        return ProviderReadiness.Ready;
    }

    public static bool IsFasterWhisperDownloaded(string model)
    {
        string hfCache = GetHuggingFaceCacheDir();
        string modelPath = Path.Combine(hfCache, $"models--Systran--faster-whisper-{model}");
        
        // Check if the model directory exists
        if (!Directory.Exists(modelPath))
            return false;
            
        // Check for either refs/main (standard HF structure) or any model files
        string refsPath = Path.Combine(modelPath, "refs", "main");
        if (File.Exists(refsPath))
            return true;
            
        // Fallback: check if there are any model files in the directory
        try
        {
            return Directory.GetFiles(modelPath, "*.bin").Length > 0 ||
                   Directory.GetFiles(modelPath, "*.json").Length > 0 ||
                   Directory.GetFiles(modelPath, "*.model").Length > 0;
        }
        catch
        {
            // If we can't read the directory, assume not downloaded
            return false;
        }
    }

    public static bool IsNllbDownloaded(string model)
    {
        string hfCache = GetHuggingFaceCacheDir();
        string modelPath = Path.Combine(hfCache, $"models--facebook--{model}");
        
        // Check if the model directory exists
        if (!Directory.Exists(modelPath))
            return false;
            
        // Check for either refs/main (standard HF structure) or any model files
        string refsPath = Path.Combine(modelPath, "refs", "main");
        if (File.Exists(refsPath))
            return true;
            
        // Fallback: check if there are any model files in the directory
        try
        {
            return Directory.GetFiles(modelPath, "*.bin").Length > 0 ||
                   Directory.GetFiles(modelPath, "*.json").Length > 0 ||
                   Directory.GetFiles(modelPath, "*.model").Length > 0;
        }
        catch
        {
            // If we can't read the directory, assume not downloaded
            return false;
        }
    }

    public static bool IsPiperVoiceDownloaded(string voice, string? piperDir)
    {
        if (string.IsNullOrEmpty(piperDir) || !Directory.Exists(piperDir)) return false;
        string onnxPath = Path.Combine(piperDir, $"{voice}.onnx");
        string jsonPath = Path.Combine(piperDir, $"{voice}.onnx.json");
        return File.Exists(onnxPath) && File.Exists(jsonPath);
    }

    private static string GetHuggingFaceCacheDir()
    {
        string? envCache = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrEmpty(envCache)) return Path.Combine(envCache, "hub");
        if (!string.IsNullOrEmpty(envCache)) return envCache;
        string? hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrEmpty(hfHome)) return Path.Combine(hfHome, "hub");

        // HuggingFace default is ~/.cache/huggingface/hub
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".cache", "huggingface", "hub");
    }
}
