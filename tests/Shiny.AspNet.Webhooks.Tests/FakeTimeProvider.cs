namespace Shiny.AspNet.Webhooks.Tests;

/// <summary>A clock that only moves when told to.</summary>
sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => this.now;

    public void Advance(TimeSpan by) => this.now += by;
}
