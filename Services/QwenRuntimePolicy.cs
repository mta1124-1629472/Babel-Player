using System;

namespace Babel.Player.Services;

public static class QwenRuntimePolicy
{
    public const string MaxConcurrencyEnvironmentVariable = "BABEL_QWEN_MAX_CONCURRENCY";
    private const int DefaultMaxConcurrency = 1;
    private const int MinMaxConcurrency = 1;
    private const int MaxMaxConcurrency = 2;

    public static int ResolveMaxConcurrency()
    {
        var rawValue = Environment.GetEnvironmentVariable(MaxConcurrencyEnvironmentVariable);
        if (!int.TryParse(rawValue, out var parsed))
            return DefaultMaxConcurrency;

        return Math.Clamp(parsed, MinMaxConcurrency, MaxMaxConcurrency);
    }
}
