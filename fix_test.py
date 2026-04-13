import re

with open('BabelPlayer.Tests/WeSpeakerCpuDiarizationProviderTests.cs', 'r') as f:
    content = f.read()

# Fix ComputeMarkerHash reference
old_code = """        var markerPath = manager.GetBootstrapMarkerPath();
        File.WriteAllText(markerPath, ComputeMarkerHash(requirementsPath));"""

new_code = """        var markerPath = manager.GetBootstrapMarkerPath();

        var fileContent = File.ReadAllText(requirementsPath);
        var hashContent = $"python:3.11.6\\n{fileContent}";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashContent));
        File.WriteAllText(markerPath, Convert.ToHexString(bytes));"""

content = content.replace(old_code, new_code)

with open('BabelPlayer.Tests/WeSpeakerCpuDiarizationProviderTests.cs', 'w') as f:
    f.write(content)
