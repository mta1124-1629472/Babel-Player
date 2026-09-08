using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.Tokenizers;

namespace Babel.Player.Services.Chatterbox;

internal sealed class ChatterboxTokenizer
{
    private readonly BpeTokenizer _tokenizer;

    private ChatterboxTokenizer(BpeTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    public static async Task<ChatterboxTokenizer> LoadAsync(string tokenizerPath, CancellationToken cancellationToken = default)
    {
        var tokenizerText = await File.ReadAllTextAsync(tokenizerPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(tokenizerText);
        var root = document.RootElement;
        var model = root.GetProperty("model");
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in model.GetProperty("vocab").EnumerateObject())
            vocabulary[entry.Name] = entry.Value.GetInt32();

        var merges = new List<string>();
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            if (merge.ValueKind is JsonValueKind.String)
            {
                merges.Add(merge.GetString() ?? string.Empty);
                continue;
            }

            if (merge.ValueKind is JsonValueKind.Array)
            {
                var parts = merge.EnumerateArray()
                    .Select(part => part.GetString() ?? string.Empty)
                    .ToArray();
                if (parts.Length == 2)
                    merges.Add($"{parts[0]} {parts[1]}");
            }
        }

        var options = new BpeOptions(vocabulary)
        {
            Merges = merges,
            SpecialTokens = ReadSpecialTokens(root),
            UnknownToken = "[UNK]",
            ByteLevel = true,
        };
        return new ChatterboxTokenizer(BpeTokenizer.Create(options));
    }

    public long[] Encode(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return _tokenizer
            .EncodeToIds(text.Trim(), false, false)
            .Select(static tokenId => (long)tokenId)
            .ToArray();
    }

    private static Dictionary<string, int> ReadSpecialTokens(JsonElement root)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("added_tokens", out var addedTokens) ||
            addedTokens.ValueKind is not JsonValueKind.Array)
        {
            return tokens;
        }

        foreach (var token in addedTokens.EnumerateArray())
        {
            if (!token.TryGetProperty("content", out var contentElement) ||
                !token.TryGetProperty("id", out var idElement) ||
                contentElement.ValueKind is not JsonValueKind.String ||
                idElement.ValueKind is not JsonValueKind.Number)
            {
                continue;
            }

            var content = contentElement.GetString();
            if (!string.IsNullOrWhiteSpace(content) && idElement.TryGetInt32(out int id))
                tokens[content] = id;
        }

        return tokens;
    }
}
