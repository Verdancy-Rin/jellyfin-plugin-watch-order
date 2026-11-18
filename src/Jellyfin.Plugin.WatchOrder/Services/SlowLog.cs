using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Timing for library work. Anything slow is logged as a warning, which shows at Jellyfin's default log level, so a
/// user reporting slowness can include the line and we can see what was slow and how big it was. The rest is debug.
/// </summary>
internal static class SlowLog
{
    /// <summary>How long library work can take before it's worth a warning.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    /// <summary>Logs how long some work took: a warning when it was slow, otherwise debug.</summary>
    /// <param name="logger">The logger, or null to do nothing.</param>
    /// <param name="timer">The timer started when the work did.</param>
    /// <param name="what">What was done, e.g. "Looking up 84 films by provider id" (a message template).</param>
    /// <param name="args">Values for the template's placeholders.</param>
    public static void Took(ILogger? logger, Stopwatch timer, string what, params object?[] args)
        => Took(logger, timer.Elapsed, what, args);

    /// <summary>Logs how long some work took: a warning when it was slow, otherwise debug.</summary>
    /// <param name="logger">The logger, or null to do nothing.</param>
    /// <param name="elapsed">How long it took.</param>
    /// <param name="what">What was done (a message template).</param>
    /// <param name="args">Values for the template's placeholders.</param>
    public static void Took(ILogger? logger, TimeSpan elapsed, string what, params object?[] args)
    {
        if (logger is null)
        {
            return;
        }

        var all = args.Append(elapsed.TotalSeconds).ToArray();
        if (elapsed >= Threshold)
        {
#pragma warning disable CA2254 // The template is built from constant parts.
            logger.LogWarning(what + " took {Seconds:0.0} s, which is slow. If Watch Order is slow for you, please include this line when you report it.", all);
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(what + " took {Seconds:0.000} s", all);
#pragma warning restore CA2254
        }
    }
}
