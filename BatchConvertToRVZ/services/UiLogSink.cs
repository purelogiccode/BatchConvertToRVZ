using Serilog.Core;
using Serilog.Events;

namespace BatchConvertToRVZ.services;

/// <summary>
/// A Serilog sink that forwards rendered log messages to the application UI.
/// The MainWindow subscribes to <see cref="MessageLogged"/> to display messages
/// in the on-screen log viewer while keeping the logging pipeline decoupled from the UI.
/// </summary>
public class UiLogSink : ILogEventSink
{
    private readonly IFormatProvider? _formatProvider;

    /// <summary>
    /// Raised for every log event, providing a pre-formatted, timestamped line ready for display.
    /// </summary>
    public static event Action<string>? MessageLogged;

    public UiLogSink(IFormatProvider? formatProvider = null)
    {
        _formatProvider = formatProvider;
    }

    public void Emit(LogEvent logEvent)
    {
        var handler = MessageLogged;
        if (handler is null) return;

        var message = logEvent.RenderMessage(_formatProvider);
        var line = $"[{logEvent.Timestamp:HH:mm:ss.fff}] {message}";
        handler(line);
    }
}
