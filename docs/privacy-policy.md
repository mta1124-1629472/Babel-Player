# Privacy Policy

**Effective Date:** March 26, 2026

## 1. Data Sovereignty (The "Local First" Promise)
Babel Player is designed as a **Local-Only** application. 
*   **No Cloud Uploads:** All video processing, transcription (STT), and voice synthesis (TTS) happen entirely on your device's hardware (GPU/NPU/CPU). 
*   **No User Accounts:** We do not require sign-ups, emails, or passwords.
*   **No "Improvement" Data:** We do not collect your usage data to train our models.

## 2. Network Activity
The application will only access the internet for the following explicit user-initiated actions:
*   **Model Downloads:** When you explicitly click to download a voice model or optimization pack (e.g., from GitHub or Hugging Face).
*   **Updates:** To check for new versions of the player (can be disabled in Settings).

## 3. Telemetry & Analytics
*   **Application Telemetry:** Basic crash reporting may be active via the .NET runtime/Avalonia framework. We are actively fundraising to purchase a commercial license to disable this completely.
*   **User Tracking:** We use **Zero** marketing trackers, pixels, or cookies.

## 4. Persistence
*   **Speaker Registry:** Character voice mappings are stored locally in `%LOCALAPPDATA%\Babel-Player\`. This data never leaves your machine.
*   **Media Metadata:** Subtitles and translation caches are stored next to your media files or in your local temp folder.

## 5. User-Directed Cloud Inference (Optional)
Babel Player offers optional features that allow you to use third-party Cloud APIs (e.g., ElevenLabs, OpenAI) for higher-fidelity translation or voice synthesis.

*   **Bring Your Own Key (BYOK):** These features ONLY function if you explicitly provide your own personal API Key in the settings.
*   **Direct Connection:** When active, Babel Player establishes a direct encrypted connection between your device and the third-party provider. **Your data does not pass through Babel Player's servers.**
*   **Third-Party Privacy:** By using these features, you acknowledge that your text/audio data is subject to the privacy policy of the provider you have chosen (e.g., OpenAI's data retention policy).
*   **No Key Logging:** Your API keys are stored locally on your device using OS-level encryption (Windows DPAPI). We do not sync or collect your keys.


