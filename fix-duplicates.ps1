$content = Get-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
# Remove duplicate bootstrapRunner lines (keep only the first occurrence in each constructor)
$lines = $content -split "`n"
$inConstructor = $false
$hasBootstrapRunner = $false
$result = @()

foreach ($line in $lines) {
    if ($line -match 'var manager = new ManagedVenvHostManager\(') {
        $inConstructor = $true
        $hasBootstrapRunner = $false
        $result += $line
    }
    elseif ($line -match '\);') {
        $inConstructor = $false
        $hasBootstrapRunner = $false
        $result += $line
    }
    elseif ($inConstructor -and $line -match 'bootstrapRunner:') {
        if (-not $hasBootstrapRunner) {
            $result += $line
            $hasBootstrapRunner = $true
        }
        # Skip duplicate bootstrapRunner lines
    }
    else {
        $result += $line
    }
}

$result -join "`n" | Set-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
