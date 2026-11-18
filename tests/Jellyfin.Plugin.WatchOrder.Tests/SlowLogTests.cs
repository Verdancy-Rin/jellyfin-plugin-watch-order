using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>Slow library work shows up at Jellyfin's default log level, with what was done and how long it took.</summary>
public sealed class SlowLogTests
{
    [Fact]
    public void Slow_IsAWarningWithTheDetails()
    {
        var log = new ListLogger();
        SlowLog.Took(log, TimeSpan.FromSeconds(12.34), "Looking up {Count} {Kind} provider ids", 84, "Movie");
        var (level, message) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal("Looking up 84 Movie provider ids took 12.3 s, which is slow. If Watch Order is slow for you, please include this line when you report it.", message);
    }

    [Fact]
    public void Quick_IsOnlyDebug()
    {
        var log = new ListLogger();
        SlowLog.Took(log, TimeSpan.FromMilliseconds(40), "Loading {Count} items and their watched data", 720);
        var (level, message) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Debug, level);
        Assert.Equal("Loading 720 items and their watched data took 0.040 s", message);
    }

    [Fact]
    public void NoLogger_IsFine() => SlowLog.Took(null, TimeSpan.FromMinutes(1), "Anything");

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }
}
