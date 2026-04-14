using Babel.Player.Services;
using Babel.Player.Services.Registries;

namespace Babel.Player.Models;

/// <summary>
/// Groups the stores and provider registries that <see cref="SessionWorkflowCoordinator"/> requires.
/// All members are required — this record exists to reduce constructor parameter count, not to make
/// any of these dependencies optional.
/// </summary>
public sealed record RegistryBundle(
    PerSessionSnapshotStore PerSessionStore,
    RecentSessionsStore RecentStore,
    ITranscriptionRegistry TranscriptionRegistry,
    ITranslationRegistry TranslationRegistry,
    ITtsRegistry TtsRegistry);
