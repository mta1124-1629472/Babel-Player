# setup-qwen-coder.ps1 - Configure Qwen Coder for Babel-Player development

param(
    [string]$QwenApiKey = "",
    [string]$Provider = "openai",  # or "qwen-oauth", "openrouter"
    [string]$Model = "qwen3-coder-plus"
)

Write-Host "🔧 Setting up Qwen Coder for Babel-Player..." -ForegroundColor Cyan

# Create .qwen directory for project-specific config
$QwenDir = Join-Path $PSScriptRoot ".qwen"
if (!(Test-Path $QwenDir)) {
    New-Item -ItemType Directory -Path $QwenDir -Force | Out-Null
    Write-Host "📁 Created .qwen/ directory" -ForegroundColor Green
}

# Create .env file for Qwen Coder environment variables
$EnvFile = Join-Path $QwenDir ".env"
if (!(Test-Path $EnvFile)) {
    @"
# Qwen Coder Environment Variables for Babel-Player
# Generated: $(Get-Date -Format "yyyy-MM-dd")

# API Configuration
QWEN_API_PROVIDER=$Provider
QWEN_MODEL_NAME=$Model

# API Key (set via environment or settings.json - do not commit!)
# QWEN_API_KEY=$QwenApiKey

# Optional: Custom endpoint for self-hosted models
# QWEN_BASE_URL=

# Performance & Context
QWEN_MAX_TOKENS=8192
QWEN_TEMPERATURE=0.2

# Tool Permissions (adjust for your workflow)
QWEN_APPROVAL_MODE=default
QWEN_ALLOWED_TOOLS=run_shell_command(git),run_shell_command(dotnet),run_shell_command(python),read_file,write_file,grep,glob

# Exclude noisy env vars from context
QWEN_EXCLUDED_ENV_VARS=DEBUG,DEBUG_MODE,NODE_ENV,OPENAI_API_KEY,GEMINI_API_KEY

# Babel-Player specific context hints
BABEL_PROJECT_ROOT=$PSScriptRoot
BABEL_INFERENCE_DIR=$(Join-Path $PSScriptRoot "inference")
BABEL_TESTS_DIR=$(Join-Path $PSScriptRoot "BabelPlayer.Tests")
"@ | Out-File -FilePath $EnvFile -Encoding UTF8
    Write-Host "📝 Created $EnvFile" -ForegroundColor Green
}

# Create project settings.json for Qwen Coder
$SettingsFile = Join-Path $QwenDir "settings.json"
if (!(Test-Path $SettingsFile)) {
    $settings = @{
        general = @{
            preferredEditor = "code"  # VS Code
            enableAutoUpdate = $true
        }
        model = @{
            name = $Model
            generationConfig = @{
                temperature = 0.2
                max_tokens = 8192
            }
        }
        context = @{
            fileName = @("QWEN.md", "README.md", "docs/architecture.md")
            includeDirectories = @("Models", "Services", "ViewModels", "inference")
            fileFiltering = @{
                respectGitIgnore = $true
                respectQwenIgnore = $true
                enableFuzzySearch = $true
            }
        }
        tools = @{
            approvalMode = "default"
            allowed = @(
                "run_shell_command(git status)",
                "run_shell_command(git diff)",
                "run_shell_command(dotnet build)",
                "run_shell_command(dotnet test --filter 'FullyQualifiedName!~Integration')",
                "run_shell_command(python -m py_compile inference/main.py)",
                "run_shell_command(python scripts/check-architecture.py)"
            )
            exclude = @("run_shell_command(rm -rf)", "run_shell_command(del /Q /S)")
        }
        advanced = @{
            excludedEnvVars = @("DEBUG", "DEBUG_MODE", "OPENAI_API_KEY", "GEMINI_API_KEY", "DEEPL_API_KEY")
        }
    }
    $settings | ConvertTo-Json -Depth 10 | Out-File -FilePath $SettingsFile -Encoding UTF8
    Write-Host "⚙️  Created $SettingsFile" -ForegroundColor Green
}

# Create QWEN.md context file for project-specific instructions
$ContextFile = Join-Path $QwenDir "QWEN.md"
if (!(Test-Path $ContextFile)) {
    @"
# Babel-Player Project Context for Qwen Coder

## Project Overview
- **Type**: Windows desktop dubbing workstation (.NET 10 + Avalonia UI)
- **Architecture**: MVVM with SessionWorkflowCoordinator as single source of truth
- **Key Layers**: Models/ (domain), Services/ (business logic), ViewModels/ (UI logic), Views/ (XAML)
- **Python Inference**: inference/main.py (FastAPI server for transcription/translation/TTS)

## Development Commands
```bash
# Build & test
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName!~Integration"  # Unit tests only

# Python inference checks
python -m py_compile inference/main.py
python scripts/check-architecture.py  # Enforces project structure rules

# Run app (dev)
dotnet run --project BabelPlayer.csproj