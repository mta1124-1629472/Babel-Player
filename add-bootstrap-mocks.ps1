# Add bootstrap runner mocks to specific failing tests
$file = Get-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs" -Raw

# Fix the runtime validator failure test (around line 125-142)
$pattern1 = '(?s)(EnsureStartedAsync_RuntimeValidatorFailure_ReturnsExplicitManagedRuntimeMessage.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, \_\, hostPidPath, token\) =>)'
$replacement1 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern1, $replacement1

# Fix the post-start probe failure test (around line 267-293)
$pattern2 = '(?s)(EnsureStartedAsync_PostStartProbeFailureReturnsLastUnavailableDetail.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, \_\, hostPidPath, token\) =>)'
$replacement2 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern2, $replacement2

# Fix the runtime validator success test (around line 158-194)
$pattern3 = '(?s)(EnsureStartedAsync_RuntimeValidatorSuccess_StartsHostWithFloat16.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, computeType, hostPidPath, token\) =>)'
$replacement3 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern3, $replacement3

# Fix the post-start probe retries test (around line 210-251)
$pattern4 = '(?s)(EnsureStartedAsync_PostStartProbeRetriesUntilHostBecomesAvailable.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, \_\, hostPidPath, token\) =>)'
$replacement4 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern4, $replacement4

# Fix the health unavailable with tracked host test (around line 490-527)
$pattern5 = '(?s)(EnsureStartedAsync_HealthUnavailableWithTrackedHost_RestartsInsteadOfReusing.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, \_\, hostPidPath, token\) =>)'
$replacement5 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern5, $replacement5

# Fix the stale PID file test (around line 580-617)
$pattern6 = '(?s)(EnsureStartedAsync_StalePidFileWithoutLiveProcess_RemovesItAndContinues.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, \_\, hostPidPath, token\) =>)'
$replacement6 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern6, $replacement6

# Fix the matching bootstrap version test (around line 380-417)
$pattern7 = '(?s)(EnsureStartedAsync_MatchingBootstrapVersion_SkipsBootstrap.*?constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),)(.*?hostProcessStarter: \(\_\, \_\, \_\, hostPidPath, token\) =>)'
$replacement7 = '$1' + "`n            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>`n                Task.CompletedTask,`n            $2"
$file = $file -replace $pattern7, $replacement7

$file | Set-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
