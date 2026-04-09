$content = Get-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
$fixedContent = $content -replace '            constraintsPathResolver: \(\) => Path\.Combine\(_dir, "gpu-constraints\.txt"\),', '            constraintsPathResolver: () => Path.Combine(_dir, "gpu-constraints.txt"),
            bootstrapRunner: (uvPath, venvDir, pythonPath, requirementsPath, constraintsPath, token) =>
                Task.CompletedTask,'
$fixedContent | Set-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
