using System;

namespace Babel.Player.Services;

/// <summary>
/// Thrown when a pipeline stage cannot run because the selected provider
/// is not implemented, requires an API key that is not present, or the
/// selected model is invalid for the selected provider.
/// </summary>
public sealed class PipelineProviderException : InvalidOperationException
{
    public PipelineProviderException(string message) : base(message) { }
}
