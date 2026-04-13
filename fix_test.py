import sys

file_path = "BabelPlayer.Tests/WeSpeakerCpuDiarizationProviderTests.cs"
with open(file_path, "r") as f:
    content = f.read()

replacement = """        var manager = new ManagedCpuRuntimeManager(
            _log,
            cpuRuntimeRootResolver: () => runtimeRoot,
            requirementsPathResolver: () => requirementsPath);

        // Force state to Ready so CheckReadiness passes
        typeof(ManagedCpuRuntimeManager).GetProperty("State")?.SetValue(manager, ManagedCpuState.Ready);

        var provider = new WeSpeakerCpuDiarizationProvider(_log, manager);"""

content = content.replace("""        var manager = new ManagedCpuRuntimeManager(
            _log,
            cpuRuntimeRootResolver: () => runtimeRoot,
            requirementsPathResolver: () => requirementsPath);

        var provider = new WeSpeakerCpuDiarizationProvider(_log, manager);""", replacement)

with open(file_path, "w") as f:
    f.write(content)
