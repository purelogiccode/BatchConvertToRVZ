using RVZStudio.services;
using Xunit;

namespace RVZStudio.Tests.Services;

public class SharedHttpHandlerTests
{
    [Fact]
    public void InstanceReturnsSameObjectOnMultipleCalls()
    {
        var instance1 = SharedHttpHandler.Instance;
        var instance2 = SharedHttpHandler.Instance;

        Assert.Same(instance1, instance2);
    }

    [Fact]
    public void InstanceIsOfTypeSocketsHttpHandler()
    {
        var instance = SharedHttpHandler.Instance;

        Assert.IsType<SocketsHttpHandler>(instance);
    }

    [Fact]
    public void PooledConnectionLifetimeIsTwoMinutes()
    {
        var instance = SharedHttpHandler.Instance;

        Assert.Equal(TimeSpan.FromMinutes(2), instance.PooledConnectionLifetime);
    }

    [Fact]
    public void InstanceUsesPooledConnections()
    {
        var instance = SharedHttpHandler.Instance;

        Assert.True(instance.PooledConnectionLifetime > TimeSpan.Zero);
    }
}
