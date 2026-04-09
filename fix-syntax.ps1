$content = Get-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
# Fix the syntax error on line 719 by removing the malformed line
$lines = $content -split "`n"
$result = @()

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    # Skip the malformed line that contains the error message
    if ($line -match 'Process .uv venv --clear. failed with exit code 2.*Access is denied.*') {
        continue
    }
    $result += $line
}

$result -join "`n" | Set-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
