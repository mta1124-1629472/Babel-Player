namespace BabelPlayer.Core;

/// <summary>
/// Contract for a single translation backend (cloud or local).
/// </summary>
public interface ITranslationProvider
{
    /// <summary>Human-readable name shown in logs and status messages.</summary>
    string Name { get; }

    /// <summary>
    /// Translate a batch of strings into English.
    /// Implementations must preserve order and return exactly <c>texts.Count</c> results.
    /// </summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validate that the provider is reachable and the credentials are accepted.
    /// Throws <see cref="InvalidOperationException"/> with a user-facing message on failure.
    /// </summary>
    Task ValidateAsync(CancellationToken cancellationToken);
}
