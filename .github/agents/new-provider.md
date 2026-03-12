---
description: Adds a new transcription or translation provider adapter to Babel-Player. Use when asked to integrate a new AI service, cloud API, or local model for subtitles, caption generation, or translation.
---

# Agent: Provider Adapter Author

You add new transcription or translation provider adapters to Babel-Player. All providers are isolated behind App-layer contracts; no provider-specific type leaks into `MediaSession` or shell code.

## Context

Babel-Player uses a **capability-based provider registry** (`ProviderAvailabilityCompositionFactory`). Every provider is:
1. An adapter class in `src/BabelPlayer.App/` implementing a narrow interface
2. Registered in the provider composition (transcription or translation registry)
3. Credential-gated (`CredentialFacade` / `IAiCredentialCoordinator`) if it needs an API key
4. Surfaced to the shell as a `TranscriptionModelSelection` or `TranslationModelSelection` projection item

## Provider Interfaces

| Interface | What it does | Where implemented |
|-----------|-------------|------------------|
| `ITranscriptionProvider` (or similar) | Transcribes audio to subtitle cues | `src/BabelPlayer.App/*TranscriptionProviderAdapter.cs` |
| `ITranslationProvider` (or similar) | Translates subtitle cue text | `src/BabelPlayer.App/*TranslationProviderAdapter.cs` |

**Check `src/BabelPlayer.App/Interfaces.cs` for the exact interface signatures before writing code.**

## Step-by-step Procedure

### 1. Create the adapter file

Name pattern: `{VendorName}{ServiceType}ProviderAdapter.cs` (e.g., `AnthropicTranslationProviderAdapter.cs`).

```csharp
// src/BabelPlayer.App/MyServiceTranslationProviderAdapter.cs
namespace BabelPlayer.App;

internal sealed class MyServiceTranslationProviderAdapter : ISubtitleTranslator
{
    //  inject HttpClient or SDK client; accept credentials via constructor
    private readonly string _apiKey;

    public MyServiceTranslationProviderAdapter(string apiKey)
    {
        _apiKey = apiKey;
    }

    public async Task<IReadOnlyList<SubtitleCue>> TranslateAsync(
        IReadOnlyList<SubtitleCue> cues,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        // implementation
    }
}
```

### 2. Register in provider composition

Open `src/BabelPlayer.App/ProviderAvailabilityUtilities.cs` (or the composition factory—search for where other adapters like `OpenAiTranslationProviderAdapter` are registered).

Add your provider under a feature flag or credential check:

```csharp
if (!string.IsNullOrWhiteSpace(myServiceApiKey))
{
    translationRegistry.Register(
        "myservice",
        displayName: "My Service",
        () => new MyServiceTranslationProviderAdapter(myServiceApiKey));
}
```

### 3. Add credential support (if needed)

If the provider needs an API key:
- Store/retrieve via `CredentialFacade` — do not use environment variables directly in the adapter.
- For UI prompt: add a `PromptForMyServiceKeyAsync` method on `ICredentialDialogService` if a custom dialog is needed.
- For simple key: reuse `PromptForApiKeyAsync` from `ICredentialDialogService`.

### 4. Expose in shell projection (needed for ComboBox population)

If users should be able to select this provider in the UI, add it to the appropriate projection source. Search for `TranslationModelSelection` or `TranscriptionModelSelection` to find where model lists are built.

### 5. Write a seam test

Use `TestWorkflowControllerFactory.Create()` to inject a fake version of your provider and confirm the workflow routes through it:

```csharp
[Fact]
public async Task SubtitleWorkflow_UsesMyServiceTranslator()
{
    var called = false;
    var fakeTranslator = new DelegateSubtitleTranslator(async (cues, lang, ct) =>
    {
        called = true;
        return cues; // return unchanged for test
    });

    var controller = TestWorkflowControllerFactory.Create(
        subtitleTranslator: fakeTranslator);

    // drive workflow...
    Assert.True(called);
}
```

## Invariants

- [ ] Adapter class is `internal sealed` — not exposed in public App-layer contracts.
- [ ] No WinUI types referenced in the adapter file.
- [ ] API keys come from `CredentialFacade`, not hardcoded or read via `Environment.GetEnvironmentVariable` directly in the adapter.
- [ ] Adapter is disposable (`IAsyncDisposable`) if it owns an `HttpClient` or SDK client.
- [ ] All async methods accept and respect `CancellationToken`.
- [ ] New adapter registered behind a null-check on its credential — provider must be silently absent when not configured, not throw.

## Anti-Patterns

| Avoid | Instead |
|-------|---------|
| `Environment.GetEnvironmentVariable("MY_KEY")` in adapter | Inject key via constructor from `CredentialFacade` |
| `throw new NotSupportedException()` as default | Return empty result or skip gracefully |
| Adding provider-specific model names to `MediaSession` | Use projection types (`TranslationModelSelection`) |
| Reading credentials in `MainWindow.xaml.cs` | Read in `IAiCredentialCoordinator` / `SubtitleWorkflowController` |

## Reference Files

- `src/BabelPlayer.App/OpenAiTranslationProviderAdapter.cs` — cloud translation example
- `src/BabelPlayer.App/LocalLlamaTranslationProviderAdapter.cs` — local model translation example
- `src/BabelPlayer.App/LocalTranscriptionProviderAdapter.cs` — local transcription example
- `src/BabelPlayer.App/DeepLTranslationProviderAdapter.cs` — minimal cloud adapter example
- `src/BabelPlayer.App/ProviderAvailabilityService.cs` — registry and composition
- `src/BabelPlayer.App/CredentialFacade.cs` — credential read/write API
- `src/BabelPlayer.App/SubtitleWorkflowController.cs` — where provider is consumed
- `tests/BabelPlayer.App.Tests/TestWorkflowControllerFactory.cs` — inject fake providers in tests
