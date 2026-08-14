using Microsoft.Extensions.Logging;

namespace SpecRunner.Surfaces;

/// <summary>
/// ASP.NET Core has its own logging pipeline and will happily write to stdout on its own. That
/// would be a third diagnostic channel, which Pillar 2 rejects by definition - so the host's
/// default providers are cleared and this one is installed in their place.
///
/// This is the second of exactly two files allowed to name a logging framework (feature 8.1);
/// its whole job is to make sure nothing escapes the two surfaces. Framework messages at
/// Warning and above are hosting-level conditions - a failed bind, a request pipeline fault -
/// which feature 8.9 places on the terminal. Below Warning is host chatter that names no
/// condition, and Pillar 3's concern is failures being invisible, not routine noise being
/// absent; dropping it keeps the terminal readable without hiding anything.
/// </summary>
internal sealed class SurfaceLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new SurfaceLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class SurfaceLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += "\n" + exception;
            }

            Emit.To(
                Surface.Terminal,
                EventKinds.SelfCheck,
                message,
                Emit.Fields("source", "web-host", "category", category, "level", logLevel.ToString()));
        }
    }
}
