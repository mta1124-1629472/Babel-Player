using System;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

[Collection("Environment")]
public sealed class AppSettingsTests
{
    [Fact]
    public void EffectiveGpuServiceUrl_DockerBackend_FallsBackToPersistedValue()
    {
        var original = Environment.GetEnvironmentVariable(AppSettings.InferenceServiceUrlEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppSettings.InferenceServiceUrlEnvVar, null);
            var settings = new AppSettings
            {
                PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
                ContainerizedServiceUrl = "http://persisted:8000"
            };

            Assert.Equal("http://persisted:8000", settings.EffectiveContainerizedServiceUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppSettings.InferenceServiceUrlEnvVar, original);
        }
    }

    [Fact]
    public void EffectiveGpuServiceUrl_DockerBackend_UsesEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable(AppSettings.InferenceServiceUrlEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppSettings.InferenceServiceUrlEnvVar, "http://override:9000");
            var settings = new AppSettings
            {
                PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
                ContainerizedServiceUrl = "http://persisted:8000"
            };

            Assert.Equal("http://override:9000", settings.EffectiveContainerizedServiceUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppSettings.InferenceServiceUrlEnvVar, original);
        }
    }

    [Fact]
    public void EffectiveGpuServiceUrl_ManagedBackend_UsesManagedLoopbackUrl()
    {
        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.ManagedVenv,
            AdvancedGpuServiceUrl = "http://persisted:8000"
        };

        Assert.Equal(AppSettings.ManagedGpuServiceUrl, settings.EffectiveGpuServiceUrl);
    }

    [Fact]
    public void HdrPassthroughDefaults_PreserveConfigOnlyToneMappingSettings()
    {
        var settings = new AppSettings();

        Assert.Equal(VideoHdrPlaybackMode.Off, settings.VideoHdrPlaybackMode);
        Assert.Equal("bt.2390", settings.VideoToneMapping);
        Assert.Equal("auto", settings.VideoTargetPeak);
        Assert.True(settings.VideoHdrComputePeak);
    }

    [Fact]
    public void DiarizationProvider_DefaultsToDisabled()
    {
        var settings = new AppSettings();

        Assert.Equal(string.Empty, settings.DiarizationProvider);
    }

    [Fact]
    public void VocalSeparationEnabled_DefaultsToFalse()
    {
        var settings = new AppSettings();

        Assert.False(settings.VocalSeparationEnabled);
    }

    [Fact]
    public void PaneLayout_DefaultsMatchPlannedOpenStateAndWidths()
    {
        var settings = new AppSettings();

        Assert.True(settings.IsPipelinePaneVisible);
        Assert.True(settings.IsSegmentsPaneVisible);
        Assert.Equal(AppSettings.PipelinePaneDefaultWidth, settings.PipelinePaneWidth, precision: 3);
        Assert.Equal(AppSettings.SegmentsPaneDefaultWidth, settings.SegmentsPaneWidth, precision: 3);
        Assert.False(settings.SwapPaneSides);
    }

    [Theory]
    [InlineData(SegmentTimingMode.Pause, SegmentTimingMode.Off)]
    [InlineData(SegmentTimingMode.Off, SegmentTimingMode.Off)]
    [InlineData(SegmentTimingMode.Stretch, SegmentTimingMode.Stretch)]
    public void NormalizeRenderTimingMode_MapsPreviewOnlyModesForRender(
        SegmentTimingMode input,
        SegmentTimingMode expected)
    {
        Assert.Equal(expected, DubTimingDefaults.NormalizeRenderTimingMode(input));
    }
}
