using System;
using Babel.Player.Models;
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
}
