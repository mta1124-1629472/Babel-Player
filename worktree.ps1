[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet("new", "sync", "remove", "list", "prune")]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$Name = "",

    [string]$BaseRef = "origin/main",

    [string]$Remote = "origin",

    [switch]$DeleteBranch,

    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repoParent = [System.IO.Path]::GetFullPath((Split-Path $repoRoot -Parent))

function Get-WorktreeRoot {
    $override = $env:BABEL_PLAYER_WORKTREE_ROOT
    if (-not [string]::IsNullOrWhiteSpace($override)) {
        return [System.IO.Path]::GetFullPath($override)
    }

    $repoName = Split-Path $repoRoot -Leaf
    return [System.IO.Path]::GetFullPath((Join-Path $repoParent "$repoName.wt"))
}

$worktreesRoot = Get-WorktreeRoot

function Fail {
    param([string]$Message)
    throw $Message
}

function Info {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Cyan
}

function Success {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Green
}

function Warn {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Yellow
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Args,
        [string]$WorkingDirectory = $repoRoot,
        [switch]$AllowFailure
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & git -C $WorkingDirectory @Args 2>&1
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($null -eq $output) {
        $output = @()
    } else {
        $output = @($output | ForEach-Object {
            if ($null -eq $_) { "" } else { $_.ToString() }
        })
    }
    $exitCode = $LASTEXITCODE

    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "git $($Args -join ' ') failed:`n$output"
    }

    return @{
        Output = @($output)
        ExitCode = $exitCode
    }
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Convert-ToSafePathSegment {
    param([Parameter(Mandatory = $true)][string]$Segment)

    $safe = [regex]::Replace($Segment, '[^A-Za-z0-9._-]', '-')
    $safe = $safe.Trim('-')

    if ([string]::IsNullOrWhiteSpace($safe)) {
        $safe = "branch"
    }

    return $safe
}

function Get-DisplayPath {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath
    )

    $targetFull = [System.IO.Path]::GetFullPath($TargetPath)

    if ($targetFull -eq $repoRoot) {
        return "."
    }

    $repoParentPrefix = if ($repoParent.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $repoParent
    } else {
        $repoParent + [System.IO.Path]::DirectorySeparatorChar
    }

    if ($targetFull.StartsWith($repoParentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $targetFull.Substring($repoParentPrefix.Length)
    }

    return $targetFull
}

function Get-WorktreePathFromBranch {
    param([Parameter(Mandatory = $true)][string]$BranchName)

    $path = $worktreesRoot

    foreach ($segment in ($BranchName -split '/')) {
        $path = Join-Path $path (Convert-ToSafePathSegment -Segment $segment)
    }

    return [System.IO.Path]::GetFullPath($path)
}

function Get-WorktreeRecords {
    $lines = (Invoke-Git -Args @("worktree", "list", "--porcelain")).Output
    $records = @()
    $current = $null

    foreach ($line in $lines) {
        if ($line -like "worktree *") {
            if ($null -ne $current) {
                $records += [pscustomobject]$current
            }

            $current = [ordered]@{
                Path = $line.Substring(9).Trim()
                Branch = $null
                Locked = $false
                Detached = $false
            }

            continue
        }

        if ($null -eq $current) {
            continue
        }

        if ($line -like "branch *") {
            $current["Branch"] = ($line.Substring(7).Trim() -replace '^refs/heads/', '')
            continue
        }

        if ($line -like "locked *") {
            $current["Locked"] = $true
            continue
        }

        if ($line -eq "detached") {
            $current["Detached"] = $true
            continue
        }
    }

    if ($null -ne $current) {
        $records += [pscustomobject]$current
    }

    return $records
}

function Get-WorktreeRecordForBranch {
    param([Parameter(Mandatory = $true)][string]$BranchName)

    return Get-WorktreeRecords | Where-Object { $_.Branch -eq $BranchName } | Select-Object -First 1
}

function Test-RefExists {
    param([Parameter(Mandatory = $true)][string]$RefName)

    $result = Invoke-Git -Args @("rev-parse", "--verify", "--quiet", $RefName) -AllowFailure
    return $result.ExitCode -eq 0
}

function Assert-RepositoryClean {
    param([Parameter(Mandatory = $true)][string]$WorktreePath)

    $status = (Invoke-Git -Args @("status", "--porcelain") -WorkingDirectory $WorktreePath).Output
    if ($status.Count -gt 0) {
        Fail "Worktree has uncommitted changes. Commit, stash, or pass -Force only for removal."
    }
}

function Resolve-BranchName {
    param([string]$RequestedName)

    if (-not [string]::IsNullOrWhiteSpace($RequestedName)) {
        return $RequestedName
    }

    Fail "Branch name is required for this command."
}

Ensure-Directory -Path $worktreesRoot

switch ($Command) {
    "new" {
        $branchName = Resolve-BranchName -RequestedName $Name
        $worktreePath = Get-WorktreePathFromBranch -BranchName $branchName
        $branchRef = "refs/heads/$branchName"

        Info "Fetching $Remote..."
        Invoke-Git -Args @("fetch", $Remote, "--prune") | Out-Null

        $parentDir = Split-Path $worktreePath -Parent
        Ensure-Directory -Path $parentDir

        if (Test-Path -LiteralPath $worktreePath) {
            $existing = Get-WorktreeRecords | Where-Object { [System.IO.Path]::GetFullPath($_.Path) -eq $worktreePath } | Select-Object -First 1
            if ($null -ne $existing) {
                Fail "A git worktree already exists at $worktreePath."
            }
        }

        if (Test-RefExists -RefName $branchRef) {
            Info "Branch already exists locally. Checking it out in a new worktree..."
            Invoke-Git -Args @("worktree", "add", $worktreePath, $branchName) | Out-Null
        } else {
            if (-not (Test-RefExists -RefName $BaseRef)) {
                Fail "Base ref '$BaseRef' does not exist locally after fetch."
            }

            Info "Creating $branchName from $BaseRef..."
            Invoke-Git -Args @("worktree", "add", "-b", $branchName, $worktreePath, $BaseRef) | Out-Null
        }

        Success "Worktree ready"
        Write-Host "Branch : $branchName"
        Write-Host "Path   : $worktreePath"
        Write-Host "Base   : $BaseRef"
    }

    "sync" {
        $branchName = Resolve-BranchName -RequestedName $Name
        $record = Get-WorktreeRecordForBranch -BranchName $branchName

        if ($null -eq $record) {
            Fail "No worktree is currently tracking branch '$branchName'."
        }

        $worktreePath = [System.IO.Path]::GetFullPath($record.Path)

        Info "Fetching $Remote..."
        Invoke-Git -Args @("fetch", $Remote, "--prune") | Out-Null

        Assert-RepositoryClean -WorktreePath $worktreePath

        if (-not (Test-RefExists -RefName $BaseRef)) {
            Fail "Base ref '$BaseRef' does not exist locally after fetch."
        }

        Info "Rebasing $branchName onto $BaseRef..."
        Invoke-Git -Args @("rebase", $BaseRef) -WorkingDirectory $worktreePath | Out-Null

        Success "Sync complete"
        Write-Host "Branch : $branchName"
        Write-Host "Path   : $worktreePath"
    }

    "remove" {
        $branchName = Resolve-BranchName -RequestedName $Name
        $record = Get-WorktreeRecordForBranch -BranchName $branchName

        if ($null -eq $record) {
            $expectedPath = Get-WorktreePathFromBranch -BranchName $branchName
            if (Test-Path -LiteralPath $expectedPath) {
                Fail "Path exists but is not registered as a git worktree: $expectedPath"
            }

            Fail "No worktree is currently tracking branch '$branchName'."
        }

        $worktreePath = [System.IO.Path]::GetFullPath($record.Path)

        if ($worktreePath -eq $repoRoot) {
            Fail "Refusing to remove the primary checkout at $repoRoot."
        }

        Info "Removing worktree $worktreePath..."
        $removeArgs = @("worktree", "remove")
        if ($Force) {
            $removeArgs += "--force"
        }
        $removeArgs += $worktreePath
        Invoke-Git -Args $removeArgs | Out-Null

        if ($DeleteBranch) {
            $deleteMode = if ($Force) { "-D" } else { "-d" }
            Info "Deleting local branch $branchName..."
            Invoke-Git -Args @("branch", $deleteMode, $branchName) | Out-Null
        }

        Invoke-Git -Args @("worktree", "prune") | Out-Null

        Success "Worktree removed"
        Write-Host "Branch : $branchName"
        Write-Host "Path   : $worktreePath"
    }

    "list" {
        $records = Get-WorktreeRecords

        if ($records.Count -eq 0) {
            Write-Host "No worktrees found."
            break
        }

        $rows = foreach ($record in $records) {
            $path = [System.IO.Path]::GetFullPath($record.Path)
            $relativePath = Get-DisplayPath -TargetPath $path

            [pscustomobject]@{
                Branch = if ($record.Branch) { $record.Branch } else { "<detached>" }
                Path   = $relativePath
                State  = if ($path -eq $repoRoot) { "root" } elseif ($record.Locked) { "locked" } else { "worktree" }
            }
        }

        $rows | Sort-Object Path | Format-Table -AutoSize
    }

    "prune" {
        Invoke-Git -Args @("worktree", "prune") | Out-Null
        Success "Worktree metadata pruned"
    }
}
