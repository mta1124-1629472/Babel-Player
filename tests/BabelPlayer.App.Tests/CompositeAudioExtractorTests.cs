using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

public sealed class CompositeAudioExtractorTests
{
    [Fact]
    public void Constructor_ThrowsArgumentException_WhenNoExtractorsProvided()
    {
        Assert.Throws<ArgumentException>(() => new CompositeAudioExtractor());
    }

    [Fact]
    public void IsAvailable_ReturnsTrue_WhenAnyExtractorIsAvailable()
    {
        var composite = new CompositeAudioExtractor(
            new FakeExtractor(isAvailable: false),
            new FakeExtractor(isAvailable: true));

        Assert.True(composite.IsAvailable);
    }

    [Fact]
    public void IsAvailable_ReturnsFalse_WhenAllExtractorsUnavailable()
    {
        var composite = new CompositeAudioExtractor(
            new FakeExtractor(isAvailable: false),
            new FakeExtractor(isAvailable: false));

        Assert.False(composite.IsAvailable);
    }

    [Fact]
    public void Extract_UsesFirstAvailableExtractor()
    {
        var first = new FakeExtractor(isAvailable: true, outputPath: "C:\\audio1.wav");
        var second = new FakeExtractor(isAvailable: true, outputPath: "C:\\audio2.wav");
        var composite = new CompositeAudioExtractor(first, second);

        var result = composite.Extract("C:\\video.mp4");

        Assert.Equal("C:\\audio1.wav", result);
        Assert.Equal(1, first.ExtractCallCount);
        Assert.Equal(0, second.ExtractCallCount);
    }

    [Fact]
    public void Extract_SkipsUnavailableExtractors()
    {
        var unavailable = new FakeExtractor(isAvailable: false, outputPath: "C:\\shouldnot.wav");
        var available = new FakeExtractor(isAvailable: true, outputPath: "C:\\correct.wav");
        var composite = new CompositeAudioExtractor(unavailable, available);

        var result = composite.Extract("C:\\video.mp4");

        Assert.Equal("C:\\correct.wav", result);
        Assert.Equal(0, unavailable.ExtractCallCount);
        Assert.Equal(1, available.ExtractCallCount);
    }

    [Fact]
    public void Extract_FallsBackToNextExtractor_WhenFirstThrows()
    {
        var throwing = new FakeExtractor(isAvailable: true, throws: true);
        var fallback = new FakeExtractor(isAvailable: true, outputPath: "C:\\fallback.wav");
        var composite = new CompositeAudioExtractor(throwing, fallback);

        var result = composite.Extract("C:\\video.mp4");

        Assert.Equal("C:\\fallback.wav", result);
    }

    [Fact]
    public void Extract_ThrowsInvalidOperationException_WhenAllExtractorsFail()
    {
        var composite = new CompositeAudioExtractor(
            new FakeExtractor(isAvailable: true, throws: true),
            new FakeExtractor(isAvailable: true, throws: true));

        Assert.Throws<InvalidOperationException>(() => composite.Extract("C:\\video.mp4"));
    }

    [Fact]
    public void Extract_ThrowsInvalidOperationException_WhenAllExtractorsUnavailable()
    {
        var composite = new CompositeAudioExtractor(
            new FakeExtractor(isAvailable: false),
            new FakeExtractor(isAvailable: false));

        Assert.Throws<InvalidOperationException>(() => composite.Extract("C:\\video.mp4"));
    }

    [Fact]
    public void Extract_InnerExceptionIsPreserved_WhenAllFail()
    {
        var throwing = new FakeExtractor(isAvailable: true, throws: true, exceptionMessage: "disk full");
        var composite = new CompositeAudioExtractor(throwing);

        var ex = Assert.Throws<InvalidOperationException>(() => composite.Extract("C:\\video.mp4"));

        Assert.NotNull(ex.InnerException);
        Assert.Contains("disk full", ex.InnerException!.Message);
    }

    private sealed class FakeExtractor : IAudioExtractor
    {
        private readonly bool _throws;
        private readonly string _exceptionMessage;
        private readonly string _outputPath;
        private int _extractCallCount;

        public FakeExtractor(bool isAvailable, string outputPath = "", bool throws = false, string exceptionMessage = "extraction failed")
        {
            IsAvailable = isAvailable;
            _outputPath = outputPath;
            _throws = throws;
            _exceptionMessage = exceptionMessage;
        }

        public bool IsAvailable { get; }
        public int ExtractCallCount => _extractCallCount;

        public string Extract(string mediaPath)
        {
            _extractCallCount++;
            if (_throws)
            {
                throw new InvalidOperationException(_exceptionMessage);
            }

            return _outputPath;
        }
    }
}
