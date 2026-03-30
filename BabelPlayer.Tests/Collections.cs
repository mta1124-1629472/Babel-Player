using Xunit;

namespace BabelPlayer.Tests;

[CollectionDefinition("Media transport", DisableParallelization = true)]
public sealed class MediaTransportCollection
{
}

[CollectionDefinition("Session workflow shared")]
public sealed class SessionWorkflowSharedCollection : ICollectionFixture<SessionWorkflowTemplateFixture>
{
}
