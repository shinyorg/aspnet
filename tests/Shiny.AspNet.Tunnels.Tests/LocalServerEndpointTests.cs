using Shiny.AspNet.Internal;

namespace Shiny.AspNet.Tunnels.Tests;

/// <summary>
/// What Kestrel reports as bound is not always something you can connect to — a wildcard is a
/// statement about which interfaces accept, not an address — so each shape is mapped to one a
/// tunnel can actually reach.
/// </summary>
public class LocalServerEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000", "127.0.0.1", 5000)]
    [InlineData("http://localhost:5001", "127.0.0.1", 5001)]
    [InlineData("http://0.0.0.0:5002", "127.0.0.1", 5002)]
    [InlineData("http://[::]:5003", "127.0.0.1", 5003)]
    [InlineData("http://*:5004", "127.0.0.1", 5004)]
    [InlineData("http://+:5005", "127.0.0.1", 5005)]
    [InlineData("http://[::1]:5006", "::1", 5006)]
    [InlineData("http://192.168.1.20:5007", "192.168.1.20", 5007)]
    [InlineData("http://127.0.0.1:5008/base", "127.0.0.1", 5008)]
    public void Maps_a_bound_address_to_one_that_can_be_reached(string address, string host, int port)
    {
        Assert.True(LocalServerEndpoint.TryParse(address, out var scheme, out var parsedHost, out var parsedPort));

        Assert.Equal("http", scheme);
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://[::1]")]
    [InlineData("unix:/tmp/kestrel.sock")]
    public void Rejects_what_has_no_port(string address)
        => Assert.False(LocalServerEndpoint.TryParse(address, out _, out _, out _));

    [Fact]
    public void Brackets_ipv6_for_a_url()
    {
        Assert.Equal("[::1]", LocalServerEndpoint.ForUrl("::1"));
        Assert.Equal("127.0.0.1", LocalServerEndpoint.ForUrl("127.0.0.1"));
    }
}
