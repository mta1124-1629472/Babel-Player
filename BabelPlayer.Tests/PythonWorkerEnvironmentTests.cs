using System.Diagnostics;
using System.Text;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class PythonWorkerEnvironmentTests
{
    [Fact]
    public void ApplyWorkerEnvironment_PinsUtf8StdioWithoutBom()
    {
        var psi = new ProcessStartInfo();

        PythonJsonWorkerPool<object, object>.ApplyWorkerEnvironment(psi);

        Assert.Equal("1", psi.Environment["PYTHONUNBUFFERED"]);
        Assert.Equal("1", psi.Environment["PYTHONUTF8"]);
        Assert.IsType<UTF8Encoding>(psi.StandardInputEncoding);
        Assert.IsType<UTF8Encoding>(psi.StandardOutputEncoding);
        Assert.IsType<UTF8Encoding>(psi.StandardErrorEncoding);
        Assert.Empty(psi.StandardInputEncoding.GetPreamble());
    }

    [Fact]
    public void ApplyPythonStdioEnvironment_PinsUtf8AndRespectsRedirect()
    {
        var noStdin = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        PythonSubprocessServiceBase.ApplyPythonStdioEnvironment(noStdin);

        Assert.Equal("1", noStdin.Environment["PYTHONUTF8"]);
        Assert.IsType<UTF8Encoding>(noStdin.StandardOutputEncoding);

        var withStdin = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        PythonSubprocessServiceBase.ApplyPythonStdioEnvironment(withStdin);

        Assert.IsType<UTF8Encoding>(withStdin.StandardInputEncoding);
        Assert.Empty(withStdin.StandardInputEncoding.GetPreamble());
    }

    [Fact]
    public void ApplyPythonStdioEnvironment_RespectsCallerOverride()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["PYTHONUTF8"] = "0";

        PythonSubprocessServiceBase.ApplyPythonStdioEnvironment(psi);

        Assert.Equal("0", psi.Environment["PYTHONUTF8"]);
    }
}
