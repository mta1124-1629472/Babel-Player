using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class ModelDownloader
{
    private readonly AppLog _log;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    public ModelDownloader(AppLog log)
    {
        _log = log;
    }

    public async Task<bool> DownloadFasterWhisperAsync(string model, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string repoId = $"Systran/faster-whisper-{model}";
        return await DownloadHuggingFaceModelAsync(repoId, progress, token);
    }

    public async Task<bool> DownloadNllbAsync(string model, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string repoId = $"facebook/{model}";
        return await DownloadHuggingFaceModelAsync(repoId, progress, token);
    }

    private async Task<bool> DownloadHuggingFaceModelAsync(string repoId, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string? pythonPath = DependencyLocator.FindPython();
        if (pythonPath == null)
            throw new InvalidOperationException("Python required for downloading HuggingFace models.");

        string script = @"
import sys
import os

try:
    from huggingface_hub import snapshot_download
except ImportError:
    print('Installing huggingface_hub...')
    os.system(f'""{sys.executable}"" -m pip install huggingface_hub')
    from huggingface_hub import snapshot_download

repo_id = sys.argv[1]
print(f'Downloading {repo_id}...', flush=True)
try:
    snapshot_download(repo_id=repo_id)
    print('Download complete.', flush=True)
except Exception as e:
    print(f'Error downloading: {e}', file=sys.stderr)
    sys.exit(1)
";
        string scriptPath = Path.Combine(Path.GetTempPath(), $"hf_dl_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" \"{repoId}\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            _log.Info($"Started model download for {repoId}. This may take a few minutes...");
            
            var errorReaderTask = Task.Run(async () =>
            {
                var regex = new System.Text.RegularExpressions.Regex(@"(\d+)%");
                while (true)
                {
                    string? line = await proc.StandardError.ReadLineAsync();
                    if (line == null) break;
                    
                    if (progress != null)
                    {
                        var match = regex.Match(line);
                        if (match.Success && double.TryParse(match.Groups[1].Value, out double pct))
                        {
                            progress.Report(pct / 100.0);
                        }
                    }
                }
            }, token);

            try
            {
                await proc.WaitForExitAsync(token);
                await errorReaderTask;
            }
            catch (OperationCanceledException)
            {
                proc.Kill();
                throw;
            }
            
            if (proc.ExitCode != 0)
            {
                 _log.Error($"Download failed for {repoId}", new Exception("Python downloader exited with non-zero exit code."));
                 return false;
            }
            
            _log.Info($"Download succeeded for {repoId}.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Exception during model download: {ex.Message}", ex);
            return false;
        }
        finally
        {
             if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    public async Task<bool> DownloadPiperVoiceAsync(string voiceName, string? piperDir, IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(piperDir))
        {
            _log.Error("PiperModelDir is not configured.", new InvalidOperationException("PiperModelDir is null"));
            return false;
        }

        Directory.CreateDirectory(piperDir);
        string onnxPath = Path.Combine(piperDir, $"{voiceName}.onnx");
        string jsonPath = Path.Combine(piperDir, $"{voiceName}.onnx.json");

        // Piper voice string format: {language}_{region}-{name}-{quality}
        // Ex: en_US-lessac-medium
        string[] parts = voiceName.Split('-');
        if (parts.Length < 3)
        {
            _log.Error($"Invalid piper voice name format: {voiceName}", new ArgumentException("Invalid format"));
            return false;
        }
        
        string langRegion = parts[0]; // en_US
        string language = langRegion.Split('_')[0]; // en
        string name = parts[1];
        string quality = parts[2];

        // We build the rhasspy huggingface repository path
        string baseUrl = $"https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/{language}/{langRegion}/{name}/{quality}/{voiceName}";

        _log.Info($"Downloading Piper voice {voiceName}...");

        try
        {
            // Report fake progress to show something since piper downloads two files. 0 to 90% for onnx, 90 to 100% for json.
            var onnxProgress = new Progress<double>(p => progress?.Report(p * 0.90));
            var jsonProgress = new Progress<double>(p => progress?.Report(0.90 + p * 0.10));

            bool onnxOk = await DownloadFileAsync($"{baseUrl}.onnx", onnxPath, onnxProgress, token);
            bool jsonOk = await DownloadFileAsync($"{baseUrl}.onnx.json", jsonPath, jsonProgress, token);

            if (onnxOk && jsonOk)
            {
                _log.Info($"Piper voice {voiceName} downloaded successfully.");
                return true;
            }
            if (!onnxOk && File.Exists(onnxPath)) File.Delete(onnxPath);
            if (!jsonOk && File.Exists(jsonPath)) File.Delete(jsonPath);
            if (onnxOk && jsonOk)
            {
                _log.Info($"Piper voice {voiceName} downloaded successfully.");
                return true;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
             if (File.Exists(onnxPath)) File.Delete(onnxPath);
             if (File.Exists(jsonPath)) File.Delete(jsonPath);
             throw;
        }
        catch (Exception ex)
        {
             _log.Error($"Exception downloading Piper voice {voiceName}: {ex.Message}", ex);
             // Cleanup partial downloads
             if (File.Exists(onnxPath)) File.Delete(onnxPath);
             if (File.Exists(jsonPath)) File.Delete(jsonPath);
             return false;
        }
    }

    private async Task<bool> DownloadFileAsync(string url, string destinationPath, IProgress<double>? progress = null, CancellationToken token = default)
    {
        string tmpPath = destinationPath + ".tmp";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            using var stream = await response.Content.ReadAsStreamAsync(token);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
            {
                await fs.WriteAsync(buffer, 0, bytesRead, token);
                totalRead += bytesRead;
                if (totalBytes.HasValue)
                {
                    progress?.Report((double)totalRead / totalBytes.Value);
                }
            }
            fs.Close();

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(tmpPath, destinationPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to download {url}: {ex.Message}", ex);
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            return false;
        }
    }
}
