using System.Reflection;
using System.Threading;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class SessionWorkflowCoordinatorApiSurfaceTests
{
    [Theory]
    [InlineData(nameof(SessionWorkflowCoordinator.RegenerateSegmentTtsAsync))]
    [InlineData(nameof(SessionWorkflowCoordinator.RegenerateSegmentTranslationAsync))]
    public void PublicSegmentRegenerationApis_ExposeOptionalCancellationToken(string methodName)
    {
        var method = typeof(SessionWorkflowCoordinator).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            [typeof(string), typeof(CancellationToken)],
            modifiers: null);

        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.True(parameters[1].IsOptional);
    }
}
