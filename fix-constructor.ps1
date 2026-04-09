$content = Get-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
# Fix the incomplete constructor by adding the closing parenthesis and semicolon
$lines = $content -split "`n"
$result = @()

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    $result += $line
    
    # If this is the line with Task.CompletedTask, and the next line doesn't have the closing parenthesis, add it
    if ($line -match 'Task\.CompletedTask,') {
        $nextLine = if ($i + 1 -lt $lines.Count) { $lines[$i + 1] } else { "" }
        if ($nextLine -notmatch '\);') {
            $result += ");"
        }
    }
}

$result -join "`n" | Set-Content "BabelPlayer.Tests/ManagedVenvHostManagerTests.cs"
