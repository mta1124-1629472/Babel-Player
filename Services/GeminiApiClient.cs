using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

/// <summary>
/// Thin HTTP client for the Google Gemini REST API.
/// Supports:
///   - generateContent (text-in / text-out) for translation
///   - Files API upload + generateContent with audio part for transcription
/// </summary>
public sealed class GeminiApiClient : IDisposable
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GeminiApiClient(string apiKey, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be empty.", nameof(apiKey));

        _apiKey = apiKey.Trim();
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Text generation (translation) ─────────────────────────────────────────

    /// <summary>
    /// Sends a single user prompt to the model and returns the text response.
    /// </summary>
    public async Task<string> GenerateTextAsync(
        string model,
        string systemInstruction,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            system_instruction = new
            {
                parts = new[] { new { text = systemInstruction } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "text/plain"
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var url = $"models/{model}:generateContent?key={_apiKey}";

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException($"Gemini generateContent failed ({(int)response.StatusCode}): {payload}", response.StatusCode);

        return ExtractTextFromGenerateContentResponse(payload);
    }

    // ── Audio transcription (Files API + generateContent) ─────────────────────

    /// <summary>
    /// Uploads an audio file via the Files API, then calls generateContent
    /// with the file URI and a transcription prompt.
    /// Returns the raw text response from the model.
    /// </summary>
    public async Task<string> TranscribeAudioAsync(
        string audioFilePath,
        string model,
        string transcriptionPrompt,
        CancellationToken cancellationToken = default)
    {
        var fileUri = await UploadAudioFileAsync(audioFilePath, cancellationToken);

        var mimeType = GetAudioMimeType(audioFilePath);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new
                        {
                            fileData = new
                            {
                                mimeType = mimeType,
                                fileUri = fileUri
                            }
                        },
                        new { text = transcriptionPrompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                responseMimeType = "text/plain"
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var url = $"models/{model}:generateContent?key={_apiKey}";

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new GeminiApiException($"Gemini transcription generateContent failed ({(int)response.StatusCode}): {payload}", response.StatusCode);

        return ExtractTextFromGenerateContentResponse(payload);
    }

    // ── Files API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads a file to the Gemini Files API and returns the hosted file URI.
    /// </summary>
    private async Task<string> UploadAudioFileAsync(
        string audioFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(audioFilePath))
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");

        var mimeType = GetAudioMimeType(audioFilePath);
        var fileBytes = await File.ReadAllBytesAsync(audioFilePath, cancellationToken);
        var displayName = Path.GetFileName(audioFilePath);

        // Step 1: initiate resumable upload
        var initiateUrl = $"https://generativelanguage.googleapis.com/upload/v1beta/files?uploadType=resumable&key={_apiKey}";
        var metaJson = JsonSerializer.Serialize(new { file = new { display_name = displayName } });

        using var initiateRequest = new HttpRequestMessage(HttpMethod.Post, initiateUrl);
        initiateRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        initiateRequest.Headers.Add("X-Goog-Upload-Command", "start");
        initiateRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", fileBytes.Length.ToString());
        initiateRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        initiateRequest.Content = new StringContent(metaJson, Encoding.UTF8, "application/json");

        using var initiateResponse = await _httpClient.SendAsync(initiateRequest, cancellationToken);
        if (!initiateResponse.IsSuccessStatusCode)
        {
            var err = await initiateResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new GeminiApiException($"Gemini Files API initiate failed ({(int)initiateResponse.StatusCode}): {err}", initiateResponse.StatusCode);
        }

        // Extract upload URL from response header
        var uploadUrl = initiateResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var vals)
            ? System.Linq.Enumerable.FirstOrDefault(vals)
            : null;
        if (string.IsNullOrEmpty(uploadUrl))
            throw new GeminiApiException("Gemini Files API did not return an upload URL.", System.Net.HttpStatusCode.InternalServerError);

        // Step 2: upload the bytes
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadRequest.Content = new ByteArrayContent(fileBytes);
        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        using var uploadResponse = await _httpClient.SendAsync(uploadRequest, cancellationToken);
        var uploadPayload = await uploadResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!uploadResponse.IsSuccessStatusCode)
            throw new GeminiApiException($"Gemini Files API upload failed ({(int)uploadResponse.StatusCode}): {uploadPayload}", uploadResponse.StatusCode);

        // Parse file URI from response
        var fileNode = JsonNode.Parse(uploadPayload);
        var fileUri = fileNode?["file"]?["uri"]?.GetValue<string>()
            ?? fileNode?["uri"]?.GetValue<string>();

        if (string.IsNullOrEmpty(fileUri))
            throw new GeminiApiException("Gemini Files API upload response did not contain a file URI.", System.Net.HttpStatusCode.InternalServerError);

        return fileUri;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExtractTextFromGenerateContentResponse(string payload)
    {
        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new GeminiApiException("Failed to parse Gemini generateContent response.", System.Net.HttpStatusCode.InternalServerError);

        var text = root["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
            throw new GeminiApiException("Gemini returned an empty response.", System.Net.HttpStatusCode.InternalServerError);

        return text;
    }

    private static string GetAudioMimeType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mp3"  => "audio/mpeg",
            ".wav"  => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg"  => "audio/ogg",
            ".m4a"  => "audio/mp4",
            ".webm" => "audio/webm",
            _       => "audio/mpeg",
        };

    public void Dispose() => _httpClient.Dispose();
}

public sealed class GeminiApiException : Exception
{
    public GeminiApiException(string message, System.Net.HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
}
