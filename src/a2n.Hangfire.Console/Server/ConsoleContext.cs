using System.Globalization;
using Hangfire.Console.Serialization;
using Hangfire.Console.Storage;
using Hangfire.Server;

namespace Hangfire.Console.Server;

/// <summary>
/// Internal context for console operations during job execution.
/// </summary>
internal class ConsoleContext
{
    private readonly ConsoleId _consoleId;
    private readonly ConsoleStorage _storage;
    private readonly ConsoleOptions _options;
    private double _lastTimeOffset;
    private int _nextProgressBarId;

    public ConsoleContext(ConsoleId consoleId, ConsoleStorage storage, ConsoleOptions options)
    {
        _consoleId = consoleId;
        _storage = storage;
        _options = options;
        _storage.InitConsole(_consoleId);
    }

    public ConsoleTextColor? TextColor { get; set; }

    public static ConsoleContext? FromPerformContext(PerformContext? context)
    {
        if (context is null) return null;
        if (!context.Items.TryGetValue("ConsoleContext", out var obj)) return null;
        return obj as ConsoleContext;
    }

    public void AddLine(ConsoleLine line)
    {
        lock (this)
        {
            line.TimeOffset = Math.Round((DateTime.UtcNow - _consoleId.DateValue).TotalSeconds, 3);

            if (_lastTimeOffset >= line.TimeOffset)
                line.TimeOffset = _lastTimeOffset + 0.0001;

            _lastTimeOffset = line.TimeOffset;
            _storage.AddLine(_consoleId, line);
        }
    }

    public void WriteLine(string? value, ConsoleTextColor? color)
    {
        AddLine(new ConsoleLine
        {
            Message = value ?? "",
            TextColor = (color ?? TextColor)?.ToString()
        });
    }

    public IProgressBar WriteProgressBar(string? name, double value, ConsoleTextColor? color)
    {
        var progressBarId = Interlocked.Increment(ref _nextProgressBarId)
            .ToString(CultureInfo.InvariantCulture);

        var progressBar = new DefaultProgressBar(this, progressBarId, name, color);
        progressBar.SetValue(value);
        return progressBar;
    }

    public void Expire(TimeSpan expireIn) => _storage.Expire(_consoleId, expireIn);

    public void FixExpiration()
    {
        var ttl = _storage.GetConsoleTtl(_consoleId);
        if (ttl <= TimeSpan.Zero) return;
        _storage.Expire(_consoleId, ttl);
    }
}

internal class DefaultProgressBar : IProgressBar
{
    private readonly ConsoleContext _context;
    private readonly string _progressBarId;
    private readonly string? _name;
    private readonly ConsoleTextColor? _color;

    public DefaultProgressBar(ConsoleContext context, string progressBarId, string? name, ConsoleTextColor? color)
    {
        _context = context;
        _progressBarId = progressBarId;
        _name = name;
        _color = color;
    }

    public void SetValue(double value)
    {
        _context.AddLine(new ConsoleLine
        {
            Message = _progressBarId,
            ProgressValue = Math.Round(Math.Max(0, Math.Min(100, value)), 1),
            ProgressName = _name,
            TextColor = _color?.ToString()
        });
    }

    public void SetValue(double value, ConsoleTextColor color)
    {
        _context.AddLine(new ConsoleLine
        {
            Message = _progressBarId,
            ProgressValue = Math.Round(Math.Max(0, Math.Min(100, value)), 1),
            ProgressName = _name,
            TextColor = color.ToString()
        });
    }
}
