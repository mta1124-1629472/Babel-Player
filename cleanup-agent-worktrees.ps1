$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoPath = ""
$branchPrefixes = $null
$switches = @{
    RemoveExtraWorktrees = $false
    DeleteGoneLocalBranches = $false
    DeleteMergedLocalBranches = $false
    DeleteMergedRemoteBranches = $false
    Force = $false
    WhatIf = $false
}

for ($index = 0; $index -lt $args.Length; $index++) {
    $argument = [string]$args[$index]

    switch -Regex ($argument) {
        '^-RepoPath$' {
            if ($index + 1 -ge $args.Length) {
                throw "Missing value for -RepoPath"
            }

            $index++
            $repoPath = [string]$args[$index]
            continue
        }

        '^-BranchPrefixes$' {
            if ($index + 1 -ge $args.Length) {
                throw "Missing value for -BranchPrefixes"
            }

            $index++
            $branchPrefixes = ([string]$args[$index]).Split(',', [System.StringSplitOptions]::RemoveEmptyEntries)
            continue
        }

        '^-(RemoveExtraWorktrees|DeleteGoneLocalBranches|DeleteMergedLocalBranches|DeleteMergedRemoteBranches|Force|WhatIf)$' {
            $switches[$Matches[1]] = $true
            continue
        }

        default {
            if ($argument.StartsWith("-", [System.StringComparison]::Ordinal)) {
                throw "Unexpected argument: $argument"
            }

            if ([string]::IsNullOrWhiteSpace($repoPath)) {
                $repoPath = $argument
                continue
            }

            throw "Unexpected positional argument: $argument"
        }
    }
}

$environmentPath = Join-Path $PSScriptRoot ".codex\environments\environment.toml"
if (-not (Test-Path -LiteralPath $environmentPath)) {
    throw "Cleanup environment definition not found: $environmentPath"
}

$environmentToml = Get-Content -LiteralPath $environmentPath -Raw
$cleanupMatch = [regex]::Match(
    $environmentToml,
    "(?s)\[cleanup\]\s*script = '''\r?\n(.*?)\r?\n'''")

if (-not $cleanupMatch.Success) {
    throw "Could not locate [cleanup].script in $environmentPath"
}

$cleanupScript = [scriptblock]::Create($cleanupMatch.Groups[1].Value)
$forwardedParameters = @{}

if (-not [string]::IsNullOrWhiteSpace($repoPath)) {
    $forwardedParameters["RepoPath"] = $repoPath
}

if ($null -ne $branchPrefixes -and $branchPrefixes.Length -gt 0) {
    $forwardedParameters["BranchPrefixes"] = $branchPrefixes
}

foreach ($switchName in @(
    "RemoveExtraWorktrees",
    "DeleteGoneLocalBranches",
    "DeleteMergedLocalBranches",
    "DeleteMergedRemoteBranches",
    "Force",
    "WhatIf"))
{
    if ($switches[$switchName]) {
        $forwardedParameters[$switchName] = $true
    }
}

& $cleanupScript @forwardedParameters
