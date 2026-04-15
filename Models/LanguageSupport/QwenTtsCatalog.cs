using System;
using System.Collections.Generic;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>Qwen3-TTS model identifiers exposed for local GPU TTS.</summary>
public static class QwenTtsCatalog
{
    public static readonly IReadOnlyList<string> ModelIds =
    [
        "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
        "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
    ];
}
