namespace BabelPlayer.Core
{
    public class AudioCaptureService
    {
        public void Start()
        {
            // Placeholder: implement WASAPI loopback capture using NAudio or CoreAudio APIs.
            // Buffer ~300ms PCM and invoke OnAudioChunk(buffer).
        }

        public void Stop()
        {
            // Stop capture
        }
    }
}
