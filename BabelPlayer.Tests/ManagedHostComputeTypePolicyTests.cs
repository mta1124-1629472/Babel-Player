using System;
using Xunit;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ManagedHostComputeTypePolicyTests
{
    [Fact]
    public void ResolveLaunchComputeType_CpuOnlyProfile_ReturnsInt8()
    {
        // Arrange
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: null, GpuVramMb: null,
            HasCuda: false, CudaVersion: null,
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: null,
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: null);

        // Act
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(hardwareSnapshot, ComputeProfile.Cpu);

        // Assert
        Assert.Equal("int8", computeType);
    }

    [Fact]
    public void ResolveLaunchComputeType_GpuProfileNoCuda_ReturnsInt8()
    {
        // Arrange: GPU profile but no CUDA available
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "NVIDIA GeForce RTX 4090",
            GpuVramMb: 24576,
            HasCuda: false, CudaVersion: null,
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: true,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: null,
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: null);

        // Act
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(hardwareSnapshot, ComputeProfile.Gpu);

        // Assert
        Assert.Equal("int8", computeType);
    }

    [Fact]
    public void ResolveLaunchComputeType_GpuWithCudaButNotBlackwell_ReturnsFloat16()
    {
        // Arrange: CUDA available but not Blackwell (e.g., RTX 4090 is Hopper, compute capability 9.0)
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "NVIDIA RTX 4090",
            GpuVramMb: 24576,
            HasCuda: true, CudaVersion: "12.8",
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: true,
            IsVsrDriverSufficient: true,
            NvidiaDriverVersion: "552.00",
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: "9.0"); // Hopper

        // Act
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(hardwareSnapshot, ComputeProfile.Gpu);

        // Assert
        Assert.Equal("float16", computeType);
    }

    [Fact]
    public void ResolveLaunchComputeType_BlackwellGpu_ReturnsFloat8()
    {
        // Arrange: Blackwell defaults to float8.
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i9",
            CpuCores: 16,
            HasAvx: true, HasAvx2: true, HasAvx512F: true,
            SystemRamGb: 64,
            GpuName: "NVIDIA B100",
            GpuVramMb: 98304,
            HasCuda: true, CudaVersion: "12.8",
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: true,
            NvidiaDriverVersion: "552.00",
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: "10.0"); // Blackwell

        // Act
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(hardwareSnapshot, ComputeProfile.Gpu);

        // Assert
        Assert.Equal("float8", computeType);
    }

    [Fact]
    public void ResolveLaunchComputeType_GpuProfileNonNvidiaFallback_ReturnsInt8()
    {
        // Arrange: GPU profile requested, but no CUDA/NVIDIA path is available.
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "AMD Ryzen 9",
            CpuCores: 12,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 64,
            GpuName: "AMD Radeon RX 7900 XTX",
            GpuVramMb: 24576,
            HasCuda: false, CudaVersion: null,
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: false,
            NvidiaDriverVersion: null,
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: null);

        // Act
        var computeType = ManagedHostComputeTypePolicy.ResolveLaunchComputeType(hardwareSnapshot, ComputeProfile.Gpu);

        // Assert
        Assert.Equal("int8", computeType);
    }

    [Fact]
    public void IsBlackwellCapable_WithBlackwellCapability_ReturnsTrue()
    {
        // Arrange
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "NVIDIA B100",
            GpuVramMb: 98304,
            HasCuda: true, CudaVersion: "12.8",
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: true,
            NvidiaDriverVersion: "552.00",
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: "10.0");

        // Act & Assert
        Assert.True(hardwareSnapshot.IsBlackwellCapable);
    }

    [Fact]
    public void IsBlackwellCapable_WithHopperCapability_ReturnsFalse()
    {
        // Arrange
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "NVIDIA RTX 4090",
            GpuVramMb: 24576,
            HasCuda: true, CudaVersion: "12.8",
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: true,
            IsVsrDriverSufficient: true,
            NvidiaDriverVersion: "552.00",
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: "9.0");

        // Act & Assert
        Assert.False(hardwareSnapshot.IsBlackwellCapable);
    }

    [Fact]
    public void IsBlackwellCapable_WithNullCapability_ReturnsFalse()
    {
        // Arrange
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "NVIDIA RTX 4090",
            GpuVramMb: 24576,
            HasCuda: true, CudaVersion: "12.8",
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: true,
            IsVsrDriverSufficient: true,
            NvidiaDriverVersion: "552.00",
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: null);

        // Act & Assert
        Assert.False(hardwareSnapshot.IsBlackwellCapable);
    }

    [Theory]
    [InlineData("10.0", true)]   // Blackwell
    [InlineData("10.1", true)]   // Blackwell variant
    [InlineData("11.0", true)]   // Future architecture
    [InlineData("9.0", false)]   // Hopper
    [InlineData("9.2", false)]   // Hopper variant
    [InlineData("8.9", false)]   // Ada
    [InlineData(null, false)]    // Null
    public void IsBlackwellCapable_VariousCapabilities_ReturnsExpected(string? capability, bool expected)
    {
        // Arrange
        var hardwareSnapshot = new HardwareSnapshot(
            IsDetecting: false,
            CpuName: "Intel Core i7",
            CpuCores: 8,
            HasAvx: true, HasAvx2: true, HasAvx512F: false,
            SystemRamGb: 32,
            GpuName: "Test GPU",
            GpuVramMb: 24576,
            HasCuda: true, CudaVersion: "12.8",
            HasOpenVino: false, OpenVinoVersion: null,
            NpuLabel: null,
            IsRtxCapable: false,
            IsVsrDriverSufficient: true,
            NvidiaDriverVersion: "552.00",
            IsHdrDisplayAvailable: false,
            GpuComputeCapability: capability);

        // Act & Assert
        Assert.Equal(expected, hardwareSnapshot.IsBlackwellCapable);
    }
}
