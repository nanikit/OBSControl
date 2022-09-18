using Microsoft.Extensions.Logging;
using System;
using System.Text;
using IPALogger = IPA.Logging.Logger;

namespace OBSControl.Utilities
{
    internal class MicrosoftToIPALogger : ILogger
    {
        [ThreadStatic]
        private static StringBuilder? _builder;

        private readonly IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
        private readonly IPALogger _logger;

        public MicrosoftToIPALogger(IPALogger logger)
        {
            _logger = logger;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return _scopeProvider.Push(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.None => IPALogger.Level.None,
                LogLevel.Trace => IPALogger.Level.Trace,
                LogLevel.Debug => IPALogger.Level.Debug,
                LogLevel.Information => IPALogger.Level.Info,
                LogLevel.Warning => IPALogger.Level.Warning,
                LogLevel.Error => IPALogger.Level.Error,
                LogLevel.Critical => IPALogger.Level.Critical,
                _ => IPALogger.Level.Warning,
            };

            var builder = _builder ??= new();
            _scopeProvider.ForEachScope((o, s) =>
            {
                builder.Append(o);
                builder.Append("| ");
            }, "");
            builder.Append(formatter(state, exception));

            _logger.Log(level, builder.ToString());
            builder.Clear();
        }
    }
}
