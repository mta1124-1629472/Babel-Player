
2025-02-27 - [Fix Base64 "Encryption" on non-Windows platforms]
Vulnerability: `FileSystemCredentialProvider.Protect()` on non-Windows used `Convert.ToBase64String()` for storing API keys which is not real encryption. Keys for OpenAI, ElevenLabs, DeepL, Google AI are stored recoverable by any user-space process.
Learning: The application assumed DPAPI would handle encryption securely on Windows, but used weak obfuscation (base64) as a fallback for non-Windows systems where DPAPI is unavailable. This is security theater.
Prevention: Ensure cross-platform security mechanisms actually implement symmetric encryption (like AES-256-GCM) with secure key derivation instead of simple encoding fallbacks.