using System.Globalization;
using Hangfire.Common;
using Hangfire.Console.Serialization;
using Hangfire.Storage;

namespace Hangfire.Console.Storage;

/// <summary>
/// Storage abstraction for console data. Reads/writes using the same format as the original Hangfire.Console.
/// </summary>
internal class ConsoleStorage : IDisposable
{
    private const int ValueFieldLimit = 256;
    private readonly JobStorageConnection _connection;

    public ConsoleStorage(IStorageConnection connection)
    {
        if (connection is not JobStorageConnection jobStorageConnection)
            throw new NotSupportedException("Storage connections must implement JobStorageConnection");
        _connection = jobStorageConnection;
    }

    public void Dispose() => _connection.Dispose();

    public void InitConsole(ConsoleId consoleId)
    {
        using var transaction = _connection.CreateWriteTransaction();
        transaction.SetRangeInHash(consoleId.GetHashKey(), [new("jobId", consoleId.JobId)]);
        transaction.Commit();
    }

    public void AddLine(ConsoleId consoleId, ConsoleLine line)
    {
        using var tran = _connection.CreateWriteTransaction();

        string? value;

        if (line.Message.Length > ValueFieldLimit - 36)
        {
            value = null;
        }
        else
        {
            value = JobHelper.ToJson(line);
            if (value.Length > ValueFieldLimit)
                value = null;
        }

        if (value is null)
        {
            var referenceKey = Guid.NewGuid().ToString("N");
            tran.SetRangeInHash(consoleId.GetHashKey(), [new(referenceKey, line.Message)]);
            line.Message = referenceKey;
            line.IsReference = true;
            value = JobHelper.ToJson(line);
        }

        tran.AddToSet(consoleId.GetSetKey(), value, line.TimeOffset);

        if (line.ProgressValue.HasValue && line.Message == "1")
        {
            var progress = line.ProgressValue.Value.ToString(CultureInfo.InvariantCulture);
            tran.SetRangeInHash(consoleId.GetHashKey(), [new("progress", progress)]);
        }

        tran.Commit();
    }

    public int GetLineCount(ConsoleId consoleId)
    {
        var result = (int)_connection.GetSetCount(consoleId.GetSetKey());
        if (result == 0)
            return (int)_connection.GetSetCount(consoleId.GetOldConsoleKey());
        return result;
    }

    public IEnumerable<ConsoleLine> GetLines(ConsoleId consoleId, int start, int end)
    {
        var useOldKeys = false;
        var items = _connection.GetRangeFromSet(consoleId.GetSetKey(), start, end);

        if (items is null || items.Count == 0)
        {
            items = _connection.GetRangeFromSet(consoleId.GetOldConsoleKey(), start, end);
            useOldKeys = true;
        }

        foreach (var item in items)
        {
            var line = JobHelper.FromJson<ConsoleLine>(item);

            if (line.IsReference)
            {
                var hashKey = useOldKeys ? consoleId.GetOldConsoleKey() : consoleId.GetHashKey();
                try
                {
                    line.Message = _connection.GetValueFromHash(hashKey, line.Message) ?? line.Message;
                }
                catch { /* ignore read errors for old format */ }
                line.IsReference = false;
            }

            yield return line;
        }
    }

    public void Expire(ConsoleId consoleId, TimeSpan expireIn)
    {
        using var tran = (JobStorageTransaction)_connection.CreateWriteTransaction();
        tran.ExpireSet(consoleId.GetSetKey(), expireIn);
        tran.ExpireHash(consoleId.GetHashKey(), expireIn);
        tran.Commit();
    }

    public TimeSpan GetConsoleTtl(ConsoleId consoleId)
    {
        return _connection.GetHashTtl(consoleId.GetHashKey());
    }

    public StateData? GetState(ConsoleId consoleId)
    {
        return _connection.GetStateData(consoleId.JobId);
    }
}
