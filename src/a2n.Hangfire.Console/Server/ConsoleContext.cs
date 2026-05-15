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

        var progressBar = new DefaultProgressBar(this, progressBarId, _options.ProgressBarDecimalDigits, name, color);
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

/// <summary>
/// Default progress bar implementation that writes updates to storage.
/// </summary>
internal class DefaultProgressBar : IProgressBar
{
    private readonly ConsoleContext _context;
    private readonly string _progressBarId;
    private readonly int _decimalDigits;
    private string? _name;
    private string? _color;
    private double _value;

    internal DefaultProgressBar(ConsoleContext context, string progressBarId, int decimalDigits, string? name, ConsoleTextColor? color)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _progressBarId = progressBarId ?? throw new ArgumentNullException(nameof(progressBarId));
        _decimalDigits = decimalDigits;
        _name = name;
        _color = color?.ToString();
        _value = -1;
    }

    public void SetValue(int value)
    {
        SetValue((double)value);
    }

    public void SetValue(double value)
    {
        value = Math.Round(value, _decimalDigits);

        if (value < 0 || value > 100)
            throw new ArgumentOutOfRangeException(nameof(value), "Value should be in range 0..100");

        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (Interlocked.Exchange(ref _value, value) == value) return;

        _context.AddLine(new ConsoleLine
        {
            Message = _progressBarId,
            ProgressName = _name,
            ProgressValue = value,
            TextColor = _color
        });

        _name = null; // write name only once
        _color = null; // write color only once
    }
}

/// <summary>
/// No-op progress bar used when console context is not available.
/// </summary>
internal class NoOpProgressBar : IProgressBar
{
    public void SetValue(int value)
    {
        SetValue((double)value);
    }

    public void SetValue(double value)
    {
        value = Math.Round(value, 1);

        if (value < 0 || value > 100)
            throw new ArgumentOutOfRangeException(nameof(value), "Value should be in range 0..100");
    }
}
