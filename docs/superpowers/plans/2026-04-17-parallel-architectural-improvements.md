# Parallel Architectural Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the managed-backend warmup dialog behind a dialog service, serialize `CurrentSession` writes in `SessionWorkflowCoordinator`, and add shared retry/rate-limit handling for all cloud HTTP clients without breaking Babel Player.

**Architecture:** Keep Avalonia-only UI construction inside a dedicated dialog service so `MainWindowViewModel` only decides whether the notice should appear and whether the opt-out flag must be persisted. Protect `SessionWorkflowCoordinator.CurrentSession` mutation sites with a single coordinator-owned lock so background-thread `with` reassignments stop racing each other. Centralize cloud retry logic in one helper used by OpenAI, DeepL, ElevenLabs, Google, and Gemini API clients so `429`, `408`, transient `5xx`, and `Retry-After` handling stay consistent.

**Tech Stack:** C# / .NET 10, Avalonia 12.0.1, CommunityToolkit.Mvvm, `HttpClient`, xUnit

---

## Scope Notes

- `.github/workflows/ci.yml` already exists and is out of scope.
- `Services/IMediaTransportManager.cs` already extends `IDisposable`; no change is required.
- This scope spans three independent subsystems. The tasks below keep them isolated so they can still land as separate commits even though the user asked for one combined plan.

## File Structure

**Create:**
- `Services/IDialogService.cs`
  Responsibility: narrow abstraction for the managed-backend warmup notice so the view model no longer constructs Avalonia controls directly.
- `Services/AvaloniaDialogService.cs`
  Responsibility: current warmup notice window construction and modal display logic, moved out of `MainWindowViewModel`.
- `Services/HttpRetryHelper.cs`
  Responsibility: shared retry helper for outbound cloud `HttpClient` calls with exponential backoff and `Retry-After` support.
- `BabelPlayer.Tests/MainWindowViewModelTests.cs`
  Responsibility: regression tests for the warmup-notice orchestration and settings persistence behavior.
- `BabelPlayer.Tests/SessionWorkflowCoordinatorLockingTests.cs`
  Responsibility: source-based regression guard that fails when a `CurrentSession =` mutation is added outside `lock (_sessionLock)`.
- `BabelPlayer.Tests/HttpRetryHelperTests.cs`
  Responsibility: unit tests for retry-count, exponential backoff, and `Retry-After` handling.
- `BabelPlayer.Tests/CloudApiClientRetryTests.cs`
  Responsibility: integration-style tests proving every cloud API client actually routes through the shared retry helper.

**Modify:**
- `ViewModels/MainWindowViewModel.cs`
  Responsibility: inject `IDialogService`, replace inline Avalonia control construction, keep the settings save behavior in the view model.
- `Views/MainWindow.axaml.cs`
  Responsibility: call the view-model warmup method without passing an owner if the dialog service resolves the main window itself.
- `App.axaml.cs`
  Responsibility: compose and inject `AvaloniaDialogService` into `MainWindowViewModel`.
- `Services/OpenAiApiClient.cs`
  Responsibility: migrate existing retry usage to `HttpRetryHelper`.
- `Services/DeepLApiClient.cs`
  Responsibility: wrap usage and translation calls in shared retry behavior.
- `Services/ElevenLabsApiClient.cs`
  Responsibility: wrap subscription and speech-download calls in shared retry behavior.
- `Services/GoogleApiClient.cs`
  Responsibility: wrap voices, synthesize, and speech-recognition calls in shared retry behavior.
- `Services/GeminiApiClient.cs`
  Responsibility: wrap `generateContent` and Files API upload calls in shared retry behavior.
- `Services/SessionWorkflowCoordinator.cs`
  Responsibility: declare `_sessionLock` and lock root-file `CurrentSession` assignments.
- `Services/SessionWorkflowCoordinator.DevTools.cs`
  Responsibility: lock `CurrentSession` reset assignment in dev-only reset flow.
- `Services/SessionWorkflowCoordinator.Pipeline.cs`
  Responsibility: lock all pipeline-stage `CurrentSession` assignments.
- `Services/SessionWorkflowCoordinator.Playback.cs`
  Responsibility: lock all playback-driven `CurrentSession` assignments.
- `Services/SessionWorkflowCoordinator.SpeakerRelabel.cs`
  Responsibility: lock speaker relabel `CurrentSession` assignment.
- `Services/SessionWorkflowCoordinator.TtsReference.cs`
  Responsibility: lock TTS reference `CurrentSession` assignments.

**Leave Alone:**
- `Services/SessionWorkflowCoordinator.Orchestrators.*.cs`
  Reason: these partials currently do not contain `CurrentSession =` assignments.
- `Services/Http/HttpResilience.cs`
  Reason: do not widen scope into cleanup until the new helper is in place and all requested clients are migrated.

### Task 1: Extract The Warmup Dialog Service

**Files:**
- Create: `Services/IDialogService.cs`
- Create: `Services/AvaloniaDialogService.cs`
- Modify: `ViewModels/MainWindowViewModel.cs`
- Modify: `Views/MainWindow.axaml.cs`
- Modify: `App.axaml.cs`
- Test: `BabelPlayer.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write the failing view-model tests**

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;
using Babel.Player.ViewModels;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-mainvm-tests-{Guid.NewGuid():N}");
    private readonly AppLog _log;
    private readonly SessionSnapshotStore _store;
    private readonly PerSessionSnapshotStore _perSessionStore;
    private readonly RecentSessionsStore _recentStore;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings = new();

    public MainWindowViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _store = new SessionSnapshotStore(Path.Combine(_dir, "session.json"), _log);
        _perSessionStore = new PerSessionSnapshotStore(Path.Combine(_dir, "sessions"), _log);
        _recentStore = new RecentSessionsStore(Path.Combine(_dir, "recent-sessions.json"), _log);
        _settingsService = new SettingsService(Path.Combine(_dir, "app-settings.json"), _log);
    }

    [Fact]
    public async Task TryShowManagedBackendWarmupNoticeAsync_PersistsOptOutWhenDialogReturnsTrue()
    {
        _settings.AlwaysStartLocalGpuRuntimeAtAppStart = true;
        var coordinator = CreateCoordinator(_settings);
        coordinator.Initialize();

        var dialogService = new FakeDialogService(true);
        var viewModel = new MainWindowViewModel(
            coordinator,
            _settingsService,
            new ModelDownloader(_log),
            apiKeyStore: null,
            errorDialogService: null,
            pipelineRefreshDialogService: null,
            dialogService: dialogService,
            logFilePath: null);

        await viewModel.TryShowManagedBackendWarmupNoticeAsync();

        Assert.Equal(1, dialogService.CallCount);
        Assert.True(coordinator.CurrentSettings.ShownManagedBackendWarmupNotice);
        Assert.True(_settingsService.LoadOrDefault().ShownManagedBackendWarmupNotice);
    }

    [Fact]
    public async Task TryShowManagedBackendWarmupNoticeAsync_SkipsDialogWhenStartupWarmupIsDisabled()
    {
        _settings.AlwaysStartLocalGpuRuntimeAtAppStart = false;
        var coordinator = CreateCoordinator(_settings);
        coordinator.Initialize();

        var dialogService = new FakeDialogService(true);
        var viewModel = new MainWindowViewModel(
            coordinator,
            _settingsService,
            new ModelDownloader(_log),
            dialogService: dialogService);

        await viewModel.TryShowManagedBackendWarmupNoticeAsync();

        Assert.Equal(0, dialogService.CallCount);
        Assert.False(coordinator.CurrentSettings.ShownManagedBackendWarmupNotice);
    }

    private SessionWorkflowCoordinator CreateCoordinator(AppSettings settings)
    {
        var registries = new RegistryBundle(
            _perSessionStore,
            _recentStore,
            new TranscriptionRegistry(_log),
            new TranslationRegistry(_log),
            new TtsRegistry(_log));

        var core = new CoordinatorCoreServices(_store, _log, settings);
        return new SessionWorkflowCoordinator(core, registries);
    }

    private sealed class FakeDialogService(bool result) : IDialogService
    {
        public int CallCount { get; private set; }

        public Task<bool> ShowWarmupNoticeAsync()
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Run the new tests to confirm the seam is missing**

Run: `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter "FullyQualifiedName~MainWindowViewModelTests" -v minimal`

Expected: FAIL with compile errors for missing `IDialogService`, missing `dialogService:` constructor parameter, and `TryShowManagedBackendWarmupNoticeAsync()` not matching the new ownerless call pattern.

- [ ] **Step 3: Add the dialog abstraction and Avalonia implementation**

```csharp
// Services/IDialogService.cs
using System.Threading.Tasks;

namespace Babel.Player.Services;

public interface IDialogService
{
    Task<bool> ShowWarmupNoticeAsync();
}
```

```csharp
// Services/AvaloniaDialogService.cs
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Babel.Player.Services;

public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<bool> ShowWarmupNoticeAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(ShowWarmupNoticeAsync).ConfigureAwait(true);

        var owner = GetMainWindow();
        if (owner is null)
            return false;

        var persistDontShowAgain = false;
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "The local inference host may take 30 to 60 seconds to start. Please wait for the status to show 'Ready' before running the pipeline.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 440,
                },
            },
        };

        var dontShowAgain = new Button { Content = "Don't show again", MinWidth = 140 };
        var ok = new Button { Content = "OK", MinWidth = 96, IsDefault = true };
        var dialog = new Window
        {
            Title = "Local inference host",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        dontShowAgain.Click += (_, _) => { persistDontShowAgain = true; dialog.Close(); };
        ok.Click += (_, _) => dialog.Close();

        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Children = { dontShowAgain, ok },
        });

        await dialog.ShowDialog(owner).ConfigureAwait(true);
        return persistDontShowAgain;
    }

    private static Window? GetMainWindow() =>
        Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow as Window
            : null;
}
```

- [ ] **Step 4: Inject the service into the view model and app composition root**

```csharp
// ViewModels/MainWindowViewModel.cs
private readonly IDialogService _dialogService;

public MainWindowViewModel(
    SessionWorkflowCoordinator coordinator,
    SettingsService settingsService,
    ModelDownloader modelDownloader,
    ApiKeyStore? apiKeyStore = null,
    IErrorDialogService? errorDialogService = null,
    IPipelineRefreshDialogService? pipelineRefreshDialogService = null,
    IDialogService? dialogService = null,
    string? logFilePath = null)
{
    Coordinator = coordinator;
    _settingsService = settingsService;
    _modelDownloader = modelDownloader;
    _apiKeyStore = apiKeyStore;
    _dialogService = dialogService ?? new AvaloniaDialogService();
    ...
}

public async Task TryShowManagedBackendWarmupNoticeAsync()
{
    var settings = Coordinator.CurrentSettings;
    if (settings.ShownManagedBackendWarmupNotice)
        return;

    if (!settings.AlwaysStartLocalGpuRuntimeAtAppStart)
        return;

    var persistDontShowAgain = await _dialogService.ShowWarmupNoticeAsync().ConfigureAwait(true);
    if (!persistDontShowAgain)
        return;

    settings.ShownManagedBackendWarmupNotice = true;
    _settingsService.Save(settings);
}
```

```csharp
// Views/MainWindow.axaml.cs
protected override void OnOpened(EventArgs e)
{
    base.OnOpened(e);

    if (DataContext is MainWindowViewModel vm)
        _ = vm.TryShowManagedBackendWarmupNoticeAsync();

    ...
}
```

```csharp
// App.axaml.cs
var dialogService = new AvaloniaDialogService();

var mainVm = new MainWindowViewModel(
    _sessionWorkflowCoordinator,
    _settingsService,
    modelDownloader,
    _apiKeyStore,
    errorDialogService,
    pipelineRefreshDialogService,
    dialogService,
    logFilePath: _logFilePath);
```

- [ ] **Step 5: Run the warmup-dialog tests again**

Run: `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter "FullyQualifiedName~MainWindowViewModelTests" -v minimal`

Expected: PASS with both tests green.

- [ ] **Step 6: Commit the dialog refactor**

```bash
git add Services/IDialogService.cs Services/AvaloniaDialogService.cs ViewModels/MainWindowViewModel.cs Views/MainWindow.axaml.cs App.axaml.cs BabelPlayer.Tests/MainWindowViewModelTests.cs
git commit -m "refactor: extract warmup dialog service"
```

### Task 2: Lock `CurrentSession` Mutation Sites

**Files:**
- Create: `BabelPlayer.Tests/SessionWorkflowCoordinatorLockingTests.cs`
- Modify: `Services/SessionWorkflowCoordinator.cs`
- Modify: `Services/SessionWorkflowCoordinator.DevTools.cs`
- Modify: `Services/SessionWorkflowCoordinator.Pipeline.cs`
- Modify: `Services/SessionWorkflowCoordinator.Playback.cs`
- Modify: `Services/SessionWorkflowCoordinator.SpeakerRelabel.cs`
- Modify: `Services/SessionWorkflowCoordinator.TtsReference.cs`

- [ ] **Step 1: Write the failing source-guard test**

```csharp
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class SessionWorkflowCoordinatorLockingTests
{
    [Fact]
    public void SessionWorkflowCoordinator_CurrentSessionAssignments_AreWrappedInSessionLock()
    {
        var servicesDir = FindRepoDirectory("Services");
        var files = Directory.GetFiles(servicesDir, "SessionWorkflowCoordinator*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (!line.StartsWith("CurrentSession =", StringComparison.Ordinal))
                    continue;

                var windowStart = Math.Max(0, index - 3);
                var window = string.Join(Environment.NewLine, lines.Skip(windowStart).Take(index - windowStart + 1));
                Assert.Contains(
                    "lock (_sessionLock)",
                    window,
                    StringComparison.Ordinal);
            }
        }
    }

    private static string FindRepoDirectory(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, name);
            if (Directory.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate '{name}' from '{AppContext.BaseDirectory}'.");
    }
}
```

- [ ] **Step 2: Run the locking test to capture the current failures**

Run: `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter "FullyQualifiedName~SessionWorkflowCoordinatorLockingTests" -v minimal`

Expected: FAIL and report unguarded `CurrentSession =` assignments in:
- `Services/SessionWorkflowCoordinator.cs`
- `Services/SessionWorkflowCoordinator.DevTools.cs`
- `Services/SessionWorkflowCoordinator.Pipeline.cs`
- `Services/SessionWorkflowCoordinator.Playback.cs`
- `Services/SessionWorkflowCoordinator.SpeakerRelabel.cs`
- `Services/SessionWorkflowCoordinator.TtsReference.cs`

- [ ] **Step 3: Add the coordinator lock field**

```csharp
// Services/SessionWorkflowCoordinator.cs
private readonly object _sessionLock = new();
```

- [ ] **Step 4: Wrap every root-file `CurrentSession` write in `lock (_sessionLock)`**

```csharp
lock (_sessionLock)
{
    CurrentSession = CurrentSession with
    {
        Stage = SessionWorkflowStage.MediaLoaded,
        SourceMediaPath = sourceMediaPath,
        IngestedMediaPath = ingestedMediaPath,
        StatusMessage = "Media loaded. Ready for transcription.",
    };
}
```

Apply that exact pattern to every assignment site returned by:

```bash
Get-ChildItem Services -Filter 'SessionWorkflowCoordinator*.cs' |
  Select-String -Pattern 'CurrentSession\s*='
```

Current root-file mutation sites to wrap:
- `SessionWorkflowCoordinator.cs`: `Initialize`, `LoadMedia`, reset helpers, `RestoreSession`, `SaveCurrentSession`, `FlushPendingSave`

- [ ] **Step 5: Wrap every partial-file `CurrentSession` write without changing any surrounding logic**

```csharp
// Services/SessionWorkflowCoordinator.Playback.cs
lock (_sessionLock)
{
    CurrentSession = CurrentSession with { SpeakerVoiceAssignments = updated };
}
```

Do the same mechanical change in:
- `Services/SessionWorkflowCoordinator.DevTools.cs`
- `Services/SessionWorkflowCoordinator.Pipeline.cs`
- `Services/SessionWorkflowCoordinator.Playback.cs`
- `Services/SessionWorkflowCoordinator.SpeakerRelabel.cs`
- `Services/SessionWorkflowCoordinator.TtsReference.cs`

Do not change conditionals, object initializers, status text, save order, or any reads from `_currentSession`; only add `lock (_sessionLock)` around the assignments.

- [ ] **Step 6: Re-run the locking regression test**

Run: `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter "FullyQualifiedName~SessionWorkflowCoordinatorLockingTests" -v minimal`

Expected: PASS.

- [ ] **Step 7: Commit the locking pass**

```bash
git add Services/SessionWorkflowCoordinator.cs Services/SessionWorkflowCoordinator.DevTools.cs Services/SessionWorkflowCoordinator.Pipeline.cs Services/SessionWorkflowCoordinator.Playback.cs Services/SessionWorkflowCoordinator.SpeakerRelabel.cs Services/SessionWorkflowCoordinator.TtsReference.cs BabelPlayer.Tests/SessionWorkflowCoordinatorLockingTests.cs
git commit -m "refactor: lock current session writes"
```

### Task 3: Add Shared Cloud HTTP Retry Handling

**Files:**
- Create: `Services/HttpRetryHelper.cs`
- Create: `BabelPlayer.Tests/HttpRetryHelperTests.cs`
- Create: `BabelPlayer.Tests/CloudApiClientRetryTests.cs`
- Modify: `Services/OpenAiApiClient.cs`
- Modify: `Services/DeepLApiClient.cs`
- Modify: `Services/ElevenLabsApiClient.cs`
- Modify: `Services/GoogleApiClient.cs`
- Modify: `Services/GeminiApiClient.cs`

- [ ] **Step 1: Write helper-level tests for retry count and `Retry-After`**

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class HttpRetryHelperTests
{
    [Fact]
    public async Task SendAsync_RetriesTransientStatusCodes_UpToThreeAttempts()
    {
        var attempt = 0;

        using var response = await HttpRetryHelper.SendAsync(
            async () =>
            {
                attempt++;
                await Task.Yield();
                return attempt < 3
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            },
            delayAsync: static (_, _) => Task.CompletedTask);

        Assert.Equal(3, attempt);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_UsesRetryAfterHeader_WhenPresent()
    {
        TimeSpan? capturedDelay = null;

        using var response = await HttpRetryHelper.SendAsync(
            () =>
            {
                var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return Task.FromResult(retry);
            },
            maxAttempts: 1,
            delayAsync: (delay, _) =>
            {
                capturedDelay = delay;
                return Task.CompletedTask;
            });

        Assert.Equal(TimeSpan.FromSeconds(2), capturedDelay);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
```

- [ ] **Step 2: Write client-level retry tests for every requested cloud client**

```csharp
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class CloudApiClientRetryTests
{
    [Fact]
    public async Task OpenAiApiClient_ListModelsAsync_RetriesTooManyRequests()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"message\":\"slow down\"}}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"gpt-4o-mini\"}]}", Encoding.UTF8, "application/json")
            });

        using var client = new OpenAiApiClient("test-key", handler);
        var models = await client.ListModelsAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Contains("gpt-4o-mini", models);
    }

    [Fact]
    public async Task DeepLApiClient_GetUsageAsync_RetriesServiceUnavailable()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("busy")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"character_count\":1,\"character_limit\":1000}", Encoding.UTF8, "application/json")
            });

        using var client = new DeepLApiClient("deepl-key", handler);
        var usage = await client.GetUsageAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, usage.CharacterCount);
    }

    [Fact]
    public async Task ElevenLabsApiClient_GetSubscriptionAsync_RetriesTooManyRequests()
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("retry")
        };
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            retry,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tier\":\"starter\",\"character_count\":5,\"character_limit\":10}", Encoding.UTF8, "application/json")
            });

        using var client = new ElevenLabsApiClient("eleven-key", handler);
        var subscription = await client.GetSubscriptionAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("starter", subscription.Tier);
    }

    [Fact]
    public async Task GoogleApiClient_ListVoicesAsync_RetriesServiceUnavailable()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"message\":\"busy\"}}", Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"voices\":[{\"name\":\"en-US-Standard-A\"}]}", Encoding.UTF8, "application/json")
            });

        using var client = new GoogleApiClient("google-key", handler);
        var voices = await client.ListVoicesAsync();

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, voices.Count);
    }

    [Fact]
    public async Task GeminiApiClient_GenerateTextAsync_RetriesTooManyRequests()
    {
        var retry = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"slow down\"}", Encoding.UTF8, "application/json")
        };
        retry.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(System.TimeSpan.Zero);

        var handler = new SequencedHandler(
            retry,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"translated\"}]}}]}", Encoding.UTF8, "application/json")
            });

        using var client = new GeminiApiClient("gemini-key", handler);
        var text = await client.GenerateTextAsync("gemini-2.5-flash", "system", "prompt");

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("translated", text);
    }

    private sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
```

- [ ] **Step 3: Run the retry tests before implementing the helper**

Run: `dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter "FullyQualifiedName~HttpRetryHelperTests|FullyQualifiedName~CloudApiClientRetryTests" -v minimal`

Expected: FAIL because `HttpRetryHelper` does not exist and the non-OpenAI clients do not retry transient failures.

- [ ] **Step 4: Add the shared retry helper**

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public static class HttpRetryHelper
{
    private const int DefaultMaxAttempts = 3;

    public static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> sendAsync,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken cancellationToken = default)
    {
        delayAsync ??= Task.Delay;

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await sendAsync().ConfigureAwait(false);
                if (attempt >= maxAttempts || !ShouldRetry(response.StatusCode))
                    return response;

                var delay = GetDelay(response, attempt);
                response.Dispose();
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex, cancellationToken))
            {
                var delay = GetDelay(attempt);
                await delayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or TimeoutException
        || ex is TaskCanceledException tce && tce.CancellationToken != cancellationToken;

    private static TimeSpan GetDelay(HttpResponseMessage response, int attempt) =>
        response.Headers.RetryAfter?.Delta ?? GetDelay(attempt);

    private static TimeSpan GetDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Pow(2, attempt - 1) * 200);
}
```

- [ ] **Step 5: Migrate OpenAI to the new helper and rebuild request content per attempt**

```csharp
// Services/OpenAiApiClient.cs
public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
    ExecuteJsonAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "models"),
        async response =>
        {
            var payload = await ReadJsonAsync<ModelsResponseDto>(response, cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<string>)[.. payload.Data!.Select(model => model.Id!).Where(id => !string.IsNullOrWhiteSpace(id))];
        },
        cancellationToken);

private async Task<T> ExecuteJsonAsync<T>(
    Func<HttpRequestMessage> requestFactory,
    Func<HttpResponseMessage, Task<T>> readAsync,
    CancellationToken cancellationToken)
{
    using var response = await HttpRetryHelper.SendAsync(
        () =>
        {
            var request = requestFactory();
            return _httpClient.SendAsync(request, cancellationToken);
        },
        cancellationToken: cancellationToken).ConfigureAwait(false);

    return await readAsync(response).ConfigureAwait(false);
}
```

- [ ] **Step 6: Apply the helper to DeepL, ElevenLabs, Google, and Gemini without changing provider behavior**

```csharp
// Services/DeepLApiClient.cs / Services/ElevenLabsApiClient.cs / Services/GoogleApiClient.cs / Services/GeminiApiClient.cs
using var response = await HttpRetryHelper.SendAsync(
    async () =>
    {
        using var request = BuildRequest(...);
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    },
    cancellationToken: cancellationToken).ConfigureAwait(false);
```

Rules:
- Build a fresh `HttpRequestMessage` and fresh `HttpContent` inside the retry delegate for every attempt.
- Keep existing JSON parsing, provider-specific error mapping, and response validation intact.
- Retry only the shared cloud HTTP transport calls in the OpenAI, DeepL, ElevenLabs, Google, and Gemini clients.
- Do not widen the retry helper to local inference, container management, or non-HTTP provider paths.

- [ ] **Step 7: Re-run focused retry tests**

```bash
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --filter "FullyQualifiedName~HttpRetryHelperTests|FullyQualifiedName~CloudApiClientRetryTests" -v minimal
```

Expected: `PASS`

- [ ] **Step 8: Commit the retry changes**

```bash
git add Services/HttpRetryHelper.cs Services/OpenAiApiClient.cs Services/DeepLApiClient.cs Services/ElevenLabsApiClient.cs Services/GoogleApiClient.cs Services/GeminiApiClient.cs BabelPlayer.Tests/HttpRetryHelperTests.cs BabelPlayer.Tests/CloudApiClientRetryTests.cs
git commit -m "feat: add shared cloud http retries"
```

### Task 4: Run Full Verification

**Files**:
- None, verification only

- [ ] **Step 1: Build the solution**

```bash
dotnet build Babel-Player.sln
```

Expected: `Build succeeded.`

- [ ] **Step 2: Run the full test suite**

```bash
dotnet test Babel-Player.sln --no-build
```

Expected summary includes `Passed!`

- [ ] **Step 3: If a failure appears, tighten the repro before editing anything else**

```bash
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --no-build --filter "<paste failing test filter here>" -v minimal
```

Expected: a focused repro without unrelated edits.

## Out Of Scope

- `.github/workflows/ci.yml` already exists and stays unchanged.
- `IMediaTransportManager` already extends `IDisposable`, so no interface edit is needed.
- `Services/SessionWorkflowCoordinator*.Orchestrators.cs`, `Services/AppLog.cs`, `Services/DependencyLocator.cs`, `Services/PerSessionSnapshotStore.cs`, and `Models/WorkflowSessionSnapshot.cs` remain untouched.

## Execution Notes

- Keep the dialog extraction narrow: `MainWindowViewModel` still owns the settings gate and persistence flag, while `IDialogService` owns only the Avalonia UI construction and result.
- Keep the session locking change mechanical: add `_sessionLock` once in `Services/SessionWorkflowCoordinator.cs` and wrap each `CurrentSession = CurrentSession with { ... }` mutation in `lock (_sessionLock) { ... }` without altering surrounding logic.
- Favor a shared request helper shape for cloud clients so retries do not duplicate parsing or error handling code in each provider.
- Do not claim completion until `dotnet build` and `dotnet test` have both run successfully.
