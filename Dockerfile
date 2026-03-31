# Multi-stage build for .NET Avalonia application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS builder

WORKDIR /src

# Copy project file and restore dependencies
COPY BabelPlayer.csproj .
RUN dotnet restore

# Copy source code
COPY . .

# Build and publish
RUN dotnet publish BabelPlayer.csproj -c Release -o /app/publish

# Runtime stage - Avalonia headless/server deployment
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

# Install Avalonia runtime dependencies (for headless operation)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libx11-6 \
    libxcursor1 \
    libxrandr2 \
    libxinerama1 \
    libxi6 \
    libxss1 \
    libxkbcommon0 \
    libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=builder /app/publish .

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD dotnet BabelPlayer.dll --version || exit 1

# Default to app execution
ENTRYPOINT ["dotnet", "BabelPlayer.dll"]
