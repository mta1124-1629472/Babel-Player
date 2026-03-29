using Xunit;

namespace BabelDeck.Tests;

[CollectionDefinition("Media transport", DisableParallelization = true)]
public sealed class MediaTransportCollection
{
}

[CollectionDefinition("Session workflow shared")]
public sealed class SessionWorkflowSharedCollection : ICollectionFixture<SessionWorkflowTemplateFixture>
{
}
