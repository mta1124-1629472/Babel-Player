using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class LibMpvEmbeddedTransportTests
{
    [Fact]
    public void EvaluateVsrFilterPlan_SkipsWhenDisplaySizeIsUnavailable()
    {
        var plan = LibMpvEmbeddedTransport.EvaluateVsrFilterPlan(
            videoWidth: 1280,
            videoHeight: 720,
            displayWidth: 0,
            displayHeight: 0,
            hwPixelFormat: "nv12");

        Assert.False(plan.ShouldApply);
        Assert.Equal("display-size-unavailable", plan.Reason);
        Assert.Null(plan.FilterChain);
    }

    [Fact]
    public void EvaluateVsrFilterPlan_AppliesDirectFilterWhenUpscalingNv12()
    {
        var plan = LibMpvEmbeddedTransport.EvaluateVsrFilterPlan(
            videoWidth: 1280,
            videoHeight: 720,
            displayWidth: 3840,
            displayHeight: 2160,
            hwPixelFormat: "nv12");

        Assert.True(plan.ShouldApply);
        Assert.Equal("apply", plan.Reason);
        Assert.Equal(3.0, plan.Scale);
        Assert.NotNull(plan.FilterChain);
        Assert.Contains("@vsr:d3d11vpp=", plan.FilterChain);
        Assert.Contains("scale=3.0", plan.FilterChain);
    }

    [Fact]
    public void EvaluateVsrFilterPlan_PrependsFormatConversionForUnsupportedPixelFormat()
    {
        var plan = LibMpvEmbeddedTransport.EvaluateVsrFilterPlan(
            videoWidth: 1920,
            videoHeight: 1080,
            displayWidth: 3840,
            displayHeight: 2160,
            hwPixelFormat: "p010");

        Assert.True(plan.ShouldApply);
        Assert.NotNull(plan.FilterChain);
        Assert.Contains("lavfi=[format=nv12]", plan.FilterChain);
        Assert.Contains("d3d11vpp=", plan.FilterChain);
    }
}
