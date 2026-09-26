using System;
using System.Collections.Generic;
using FsCheck;
using FsCheck.Xunit;
using Xunit;
using a2n.Hangfire.Dashboard.Helpers;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for <see cref="FailureFingerprint"/>: message normalization, top-frame selection, and the
/// pinned v1 value. Messages are real ones from SqlClient, HttpClient, Npgsql and the BCL, because the
/// point of the rules is that recurring failures of the same kind land in one group while different
/// failures stay apart.
/// </summary>
public class FailureFingerprintTests
{
    private static string Normalize(string message) => FailureFingerprint.Compute("T", message, null).NormalizedMessage;

    private static string TopFrame(string details) => FailureFingerprint.Compute("T", "m", details).TopFrame;

    // ── Message normalization: real messages ────────────────────────────────────────────

    [Theory]
    // SqlException deadlock: the process id changes on every occurrence.
    [InlineData(
        "Transaction (Process ID 57) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.",
        "Transaction (Process ID <n>) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.")]
    // SqlException timeout: nothing variable; kept as is, including the double space.
    [InlineData(
        "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.",
        "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.")]
    [InlineData(
        "Response status code does not indicate success: 404 (Not Found).",
        "Response status code does not indicate success: <n> (Not Found).")]
    [InlineData(
        "Response status code does not indicate success: 503 (Service Unavailable).",
        "Response status code does not indicate success: <n> (Service Unavailable).")]
    // PostgresException: a numeric SQLSTATE is a number; one with letters stays.
    [InlineData(
        "23505: duplicate key value violates unique constraint \"pk_orders\"",
        "<n>: duplicate key value violates unique constraint \"pk_orders\"")]
    [InlineData("40P01: deadlock detected", "40P01: deadlock detected")]
    [InlineData("A task was canceled.", "A task was canceled.")]
    [InlineData(
        "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
        "The request was canceled due to the configured HttpClient.Timeout of <n> seconds elapsing.")]
    [InlineData(
        "Cannot insert duplicate key row in object 'dbo.Orders' with unique index 'IX_Orders_2'. The duplicate key value is (42).",
        "Cannot insert duplicate key row in object 'dbo.Orders' with unique index 'IX_Orders_2'. The duplicate key value is (<n>).")]
    public void RealMessages_Normalize(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Fact]
    public void HttpStatusCodes_StayDistinct()
    {
        var notFound = FailureFingerprint.Compute(
            "System.Net.Http.HttpRequestException",
            "Response status code does not indicate success: 404 (Not Found).",
            null);
        var unavailable = FailureFingerprint.Compute(
            "System.Net.Http.HttpRequestException",
            "Response status code does not indicate success: 503 (Service Unavailable).",
            null);

        Assert.NotEqual(notFound.Fingerprint, unavailable.Fingerprint);
    }

    [Fact]
    public void Deadlocks_WithDifferentProcessIds_ShareAFingerprint()
    {
        const string type = "Microsoft.Data.SqlClient.SqlException";
        var a = FailureFingerprint.Compute(type, "Transaction (Process ID 57) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.", null);
        var b = FailureFingerprint.Compute(type, "Transaction (Process ID 112) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.", null);

        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    // ── Message normalization: one rule at a time ───────────────────────────────────────

    [Theory]
    [InlineData("Order 3f2504e0-4f89-11d3-9a0c-0305e82c3301 not found", "Order <guid> not found")]
    [InlineData("Order {3F2504E0-4F89-11D3-9A0C-0305E82C3301} not found", "Order <guid> not found")]
    [InlineData("Order 3f2504e04f8911d39a0c0305e82c3301 not found", "Order <guid> not found")]
    [InlineData("Key cache_3f2504e0-4f89-11d3-9a0c-0305e82c3301 expired", "Key cache_<guid> expired")]
    public void Guids(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    [InlineData("Lock expired at 2026-09-25T13:20:06.2233208Z", "Lock expired at <time>")]
    [InlineData("Lock expired at 2026-09-25T13:20:06+07:00.", "Lock expired at <time>.")]
    [InlineData("Lock expired at 2026-09-25T13:20:06.123-0500", "Lock expired at <time>")]
    [InlineData("Lock expired at 2026-09-25 13:20:06", "Lock expired at <time>")]
    [InlineData("No rates for 2026-09-25", "No rates for <time>")]
    [InlineData("No rates for backup_2026-09-25.bak", "No rates for backup_<time>.bak")]
    [InlineData("Lock expired at 9/25/2026 1:20:06 PM", "Lock expired at <time>")]
    [InlineData("Lock expired at 09/25/2026", "Lock expired at <time>")]
    [InlineData("Lock expired at 25.09.2026 13:20:06", "Lock expired at <time>")]
    [InlineData("Lock expired at 2026/09/25 13:20", "Lock expired at <time>")]
    [InlineData("Lock expired at 13:20:06.123", "Lock expired at <time>")]
    [InlineData("The operation has timed out after 00:00:30.", "The operation has timed out after <time>.")]
    public void Timestamps(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    [InlineData("Exception from HRESULT: 0x80004005", "Exception from HRESULT: <hex>")]
    [InlineData("Exception from HRESULT: 0x8007000E (E_OUTOFMEMORY)", "Exception from HRESULT: <hex> (E_OUTOFMEMORY)")]
    [InlineData("Blob da39a3ee5e6b4b0d3255bfef95601890afd80709 is missing", "Blob <hex> is missing")]
    // Too short for a hash, and a hex-letters-only word is still a word.
    [InlineData("Token 3f2a is invalid", "Token 3f2a is invalid")]
    [InlineData("Token deadbeefdeadbeefdeadbeef is invalid", "Token deadbeefdeadbeefdeadbeef is invalid")]
    public void HexValues(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    [InlineData("No account for jane.doe+test@example.co.uk.", "No account for <email>.")]
    [InlineData("Mailbox 'ops@example.com' is full", "Mailbox '<email>' is full")]
    public void Emails(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    [InlineData("Connection refused (10.0.0.12:5432)", "Connection refused (<ip>)")]
    [InlineData("Host 192.168.1.10 is unreachable.", "Host <ip> is unreachable.")]
    // Five parts is a version, not an address.
    [InlineData("Unsupported protocol 1.2.3.4.5", "Unsupported protocol 1.2.3.4.5")]
    public void IpAddresses(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    [InlineData(
        "GET https://api.example.com/orders/42?page=2&size=50 failed",
        "GET https://api.example.com/orders/<n>?<query> failed")]
    [InlineData(
        "Request to 'https://api.example.com/v2/users/3f2504e0-4f89-11d3-9a0c-0305e82c3301?expand=roles' failed",
        "Request to 'https://api.example.com/v2/users/<guid>?<query>' failed")]
    [InlineData("GET http://10.0.0.5:8080/health?probe=1 failed", "GET http://<ip>/health?<query> failed")]
    // Without a query string the URL stays; ids in the path are still replaced.
    [InlineData("GET https://api.example.com/orders/42 failed", "GET https://api.example.com/orders/<n> failed")]
    public void Urls(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    [InlineData("Retry 3 of 5 took 1.5 seconds", "Retry <n> of <n> took <n> seconds")]
    [InlineData("Balance is -42", "Balance is -<n>")]
    // Digits that are part of a word or identifier stay.
    [InlineData("Unsupported format v2 in Order2Invoice on net8", "Unsupported format v2 in Order2Invoice on net8")]
    [InlineData("Timeout after 30000ms", "Timeout after 30000ms")]
    // A dotted version is left alone as a whole rather than split into numbers.
    [InlineData("Requires version 1.2.3", "Requires version 1.2.3")]
    public void Numbers(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Theory]
    // Quoted text is not replaced as a whole: these are different failures.
    [InlineData("Column 'Email' cannot be null", "Column 'Email' cannot be null")]
    [InlineData("Column 'Phone' cannot be null", "Column 'Phone' cannot be null")]
    // But the rules still apply inside quotes.
    [InlineData("Order '12345' not found", "Order '<n>' not found")]
    [InlineData("Invalid key \"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"", "Invalid key \"<guid>\"")]
    public void QuotedValues(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Fact]
    public void QuotedColumnNames_StayDistinct()
    {
        var email = FailureFingerprint.Compute("System.Data.NoNullAllowedException", "Column 'Email' cannot be null", null);
        var phone = FailureFingerprint.Compute("System.Data.NoNullAllowedException", "Column 'Phone' cannot be null", null);

        Assert.NotEqual(email.Fingerprint, phone.Fingerprint);
    }

    [Theory]
    [InlineData("Validation failed for order 42\nField 'Email' is required.", "Validation failed for order <n>")]
    [InlineData("Validation failed for order 42\r\nField 'Email' is required.", "Validation failed for order <n>")]
    [InlineData("  Validation failed  \n", "Validation failed")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OnlyTheFirstLine_Trimmed(string message, string expected)
    {
        Assert.Equal(expected, Normalize(message));
    }

    [Fact]
    public void LongMessage_IsCutAt500Characters_BeforeTheRulesRun()
    {
        var prefix = new string('x', 490);

        // " id 123456789" crosses the cap: only "123456" is left, which still reads as a number.
        var normalized = Normalize(prefix + " id 123456789");
        Assert.Equal(prefix + " id <n>", normalized);

        // Anything past the cap doesn't reach the fingerprint.
        var a = FailureFingerprint.Compute("T", new string('a', FailureFingerprint.MaxMessageLength) + " first", null);
        var b = FailureFingerprint.Compute("T", new string('a', FailureFingerprint.MaxMessageLength) + " second", null);
        Assert.Equal(new string('a', FailureFingerprint.MaxMessageLength), a.NormalizedMessage);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    // ── Top frame ───────────────────────────────────────────────────────────────────────

    private const string OrderJobFrameWithFile =
        "   at MyApp.Jobs.OrderJob.Run(Int32 orderId) in /src/MyApp/Jobs/OrderJob.cs:line 42";

    [Theory]
    [InlineData(OrderJobFrameWithFile)]
    [InlineData("   at MyApp.Jobs.OrderJob.Run(Int32 orderId) in C:\\build\\src\\MyApp\\Jobs\\OrderJob.cs:line 42")]
    [InlineData("   at MyApp.Jobs.OrderJob.Run(Int32 orderId)")]
    [InlineData("at MyApp.Jobs.OrderJob.Run(Int32 orderId, String note, CancellationToken token)")]
    public void Frame_DropsParametersAndFileInfo(string frame)
    {
        Assert.Equal("MyApp.Jobs.OrderJob.Run", TopFrame("System.InvalidOperationException: Boom\n" + frame));
    }

    [Fact]
    public void Frame_ChangedLineNumber_KeepsTheFingerprint()
    {
        const string type = "System.InvalidOperationException";
        var before = FailureFingerprint.Compute(type, "Boom", type + ": Boom\n" + OrderJobFrameWithFile);
        var after = FailureFingerprint.Compute(type, "Boom", type + ": Boom\n" + OrderJobFrameWithFile.Replace(":line 42", ":line 57"));
        var noFileInfo = FailureFingerprint.Compute(type, "Boom", type + ": Boom\n   at MyApp.Jobs.OrderJob.Run(Int32 orderId)");

        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.Fingerprint, noFileInfo.Fingerprint);
    }

    [Theory]
    // Async state machine, as older runtimes print it and as current ones do.
    [InlineData("at MyApp.Jobs.OrderJob.<RunAsync>d__7.MoveNext() in /src/OrderJob.cs:line 12", "MyApp.Jobs.OrderJob.RunAsync")]
    [InlineData("at MyApp.Jobs.OrderJob.RunAsync() in /src/OrderJob.cs:line 12", "MyApp.Jobs.OrderJob.RunAsync")]
    // Lambdas: captured (display class) and non-capturing (<>c).
    [InlineData("at MyApp.Jobs.OrderJob.<>c__DisplayClass5_0.<RunAsync>b__0(Order o)", "MyApp.Jobs.OrderJob.RunAsync")]
    [InlineData("at MyApp.Program.<>c.<Main>b__0_0(String x)", "MyApp.Program.Main")]
    // Async lambda: the state machine of a lambda.
    [InlineData("at MyApp.Jobs.OrderJob.<>c__DisplayClass5_0.<<RunAsync>b__0>d.MoveNext()", "MyApp.Jobs.OrderJob.RunAsync")]
    // Local function.
    [InlineData("at MyApp.Jobs.OrderJob.<Run>g__Helper|3_0(Int32 id)", "MyApp.Jobs.OrderJob.Run")]
    // Iterator reached through an explicit interface implementation.
    [InlineData("at MyApp.Jobs.OrderJob.<GetBatches>d__9.System.Collections.IEnumerator.MoveNext()", "MyApp.Jobs.OrderJob.GetBatches")]
    public void Frame_UnwrapsCompilerGeneratedNames(string frame, string expected)
    {
        Assert.Equal(expected, TopFrame("System.InvalidOperationException: Boom\n   " + frame));
    }

    [Theory]
    [InlineData("at MyApp.Data.Repository`1.Save(TEntity entity)", "MyApp.Data.Repository.Save")]
    [InlineData("at MyApp.Data.Repository`1.Find[TKey](TKey key)", "MyApp.Data.Repository.Find")]
    [InlineData("at MyApp.Data.Mapper.Map[TSource,TTarget](TSource source)", "MyApp.Data.Mapper.Map")]
    [InlineData("at MyApp.Data.Handler`2.<HandleAsync>d__4.MoveNext()", "MyApp.Data.Handler.HandleAsync")]
    public void Frame_DropsGenericArity(string frame, string expected)
    {
        Assert.Equal(expected, TopFrame("System.InvalidOperationException: Boom\n   " + frame));
    }

    [Fact]
    public void Frame_SkipsFrameworkFrames()
    {
        const string details =
            "System.Net.Http.HttpRequestException: Response status code does not indicate success: 503 (Service Unavailable).\r\n" +
            "   at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()\r\n" +
            "   at Microsoft.Extensions.Http.Logging.LoggingHttpMessageHandler.SendCoreAsync(HttpRequestMessage request, Boolean useAsync, CancellationToken cancellationToken)\r\n" +
            "   at Hangfire.Server.CoreBackgroundJobPerformer.InvokeMethod(PerformContext context, Object instance, Object[] arguments)\r\n" +
            "   at MyApp.Clients.BillingClient.ChargeAsync(Decimal amount) in /src/BillingClient.cs:line 88\r\n" +
            "   at MyApp.Jobs.BillingJob.RunAsync() in /src/BillingJob.cs:line 20";

        Assert.Equal("MyApp.Clients.BillingClient.ChargeAsync", TopFrame(details));
    }

    [Fact]
    public void Frame_AllFrameworkFrames_UsesTheFirst()
    {
        const string details =
            "System.Threading.Tasks.TaskCanceledException: A task was canceled.\n" +
            "   at System.Threading.Tasks.Task.ThrowIfExceptional(Boolean includeTaskCanceledExceptions)\n" +
            "   at Hangfire.Server.BackgroundJobPerformer.Perform(PerformContext context)";

        Assert.Equal("System.Threading.Tasks.Task.ThrowIfExceptional", TopFrame(details));
    }

    // A PostgresException raised through Dapper and a shared data-access helper, as two different jobs
    // would report it. Only the last frame, the job's own method, differs.
    private static string DuplicateKeyDetails(string jobFrame) =>
        "Npgsql.PostgresException: 23505: duplicate key value violates unique constraint \"pk_orders\"\n" +
        "   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(Boolean async, DataRowLoadingMode dataRowLoadingMode, Boolean readingNotifications, Boolean isReadingPrependedMessage)\n" +
        "   at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)\n" +
        "   at Npgsql.NpgsqlDataReader.NextResult(Boolean async, Boolean isConsuming, CancellationToken cancellationToken)\n" +
        "   at Dapper.SqlMapper.ExecuteImplAsync(IDbConnection cnn, CommandDefinition command, Object param)\n" +
        "   at Contoso.Data.OrderStore.InsertAsync(Order order) in /src/Contoso.Data/OrderStore.cs:line 31\n" +
        "   at " + jobFrame + "() in /src/MyApp/Jobs/Job.cs:line 18";

    private const string ImportJobFrame = "MyApp.Jobs.ImportOrdersJob.RunAsync";
    private const string SyncJobFrame = "MyApp.Jobs.SyncOrdersJob.RunAsync";

    [Fact]
    public void Frame_WithoutAJobType_SkipsLibraryFrames()
    {
        Assert.Equal("Contoso.Data.OrderStore.InsertAsync", TopFrame(DuplicateKeyDetails(ImportJobFrame)));
    }

    [Theory]
    [InlineData("MyApp.Jobs.ImportOrdersJob, MyApp")]
    [InlineData("MyApp.Jobs.ImportOrdersJob, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")]
    [InlineData("MyApp.Jobs.ImportOrdersJob")]
    [InlineData("MyApp.Jobs.Handler`1[[MyApp.Order, MyApp]], MyApp")]
    [InlineData("MyApp.Jobs.Outer+ImportOrdersJob, MyApp")]
    public void Frame_PrefersTheJobTypesRootNamespace_InEveryTypeNameForm(string jobTypeName)
    {
        var result = FailureFingerprint.Compute("Npgsql.PostgresException", "m", DuplicateKeyDetails(ImportJobFrame), jobTypeName);

        Assert.Equal(ImportJobFrame, result.TopFrame);
    }

    [Fact]
    public void SameLibraryError_FromTwoJobs_IsTwoFailures_WhenTheJobTypeIsKnown()
    {
        const string type = "Npgsql.PostgresException";
        const string message = "23505: duplicate key value violates unique constraint \"pk_orders\"";

        var import = FailureFingerprint.Compute(type, message, DuplicateKeyDetails(ImportJobFrame), "MyApp.Jobs.ImportOrdersJob, MyApp");
        var sync = FailureFingerprint.Compute(type, message, DuplicateKeyDetails(SyncJobFrame), "MyApp.Jobs.SyncOrdersJob, MyApp");
        Assert.NotEqual(import.Fingerprint, sync.Fingerprint);

        // Without the job type both stop at the shared helper, which is all the trace alone can tell.
        Assert.Equal(
            FailureFingerprint.Compute(type, message, DuplicateKeyDetails(ImportJobFrame)).Fingerprint,
            FailureFingerprint.Compute(type, message, DuplicateKeyDetails(SyncJobFrame)).Fingerprint);
    }

    [Theory]
    // No frame in the job's namespace: fall back to the first frame outside the libraries.
    [InlineData("Billing.Jobs.InvoiceJob, Billing")]
    // A job type in a library namespace (e.g. a Console.WriteLine job) says nothing about the app.
    [InlineData("System.Console, System.Console")]
    [InlineData("Hangfire.Server.BackgroundJobServer, Hangfire.Core")]
    [InlineData("")]
    [InlineData(null)]
    public void Frame_NoUsableJobNamespace_FallsBackToTheFirstApplicationFrame(string jobTypeName)
    {
        var result = FailureFingerprint.Compute("Npgsql.PostgresException", "m", DuplicateKeyDetails(ImportJobFrame), jobTypeName);

        Assert.Equal("Contoso.Data.OrderStore.InsertAsync", result.TopFrame);
    }

    [Fact]
    public void Frame_LibraryNamespaces_MatchWholeSegments()
    {
        // "MySql" is a library namespace; "MySqlHelpers" is not.
        const string details =
            "System.InvalidOperationException: Boom\n" +
            "   at MySqlConnector.MySqlCommand.ExecuteReaderAsync(CommandBehavior behavior)\n" +
            "   at MySql.Data.MySqlClient.MySqlCommand.ExecuteReader()\n" +
            "   at MySqlHelpers.Retry.Run(Action action)";

        Assert.Equal("MySqlHelpers.Retry.Run", TopFrame(details));
    }

    /// <summary>
    /// The library list is part of the v1 rules: adding a namespace changes the fingerprint of every
    /// failure whose top frame was in it. Like <see cref="Golden_V1Value"/>, a change here means a new
    /// <see cref="FailureFingerprint.VersionPrefix"/>.
    /// </summary>
    [Fact]
    public void LibraryNamespaces_ArePinnedForV1()
    {
        Assert.Equal(
            new[]
            {
                "System", "Microsoft", "Hangfire",
                "Npgsql", "Dapper", "MySqlConnector", "MySql", "Oracle", "MongoDB", "StackExchange", "Pomelo",
                "Newtonsoft",
                "Polly", "RestSharp", "Flurl", "Refit", "Grpc",
                "Azure", "Amazon", "Google",
                "RabbitMQ", "MassTransit", "Confluent",
                "Castle", "Autofac", "MediatR", "AutoMapper", "FluentValidation",
            },
            FailureFingerprint.LibraryNamespaces);
    }

    [Theory]
    [InlineData("System.InvalidOperationException: Boom")]
    [InlineData("")]
    [InlineData(null)]
    public void Frame_NoFrames_IsEmpty(string details)
    {
        Assert.Equal(string.Empty, TopFrame(details));
    }

    [Fact]
    public void Frame_MessageLineStartingWithAt_IsNotAFrame()
    {
        const string details =
            "System.ArgumentException: Invalid batch\n" +
            "at least one (1) item is required\n" +
            "   at MyApp.Jobs.BatchJob.Run()";

        Assert.Equal("MyApp.Jobs.BatchJob.Run", TopFrame(details));
    }

    [Fact]
    public void Frame_InnerException_IsTheRootCause()
    {
        // Exception text lists the inner exception's frames before the outer one's.
        const string details =
            "System.AggregateException: One or more errors occurred. (Boom) ---> System.InvalidOperationException: Boom\n" +
            "   at MyApp.Services.Inventory.Reserve(Int32 sku)\n" +
            "   --- End of inner exception stack trace ---\n" +
            "   at MyApp.Jobs.OrderJob.Run()";

        Assert.Equal("MyApp.Services.Inventory.Reserve", TopFrame(details));
    }

    [Fact]
    public void NullReferenceExceptions_FromDifferentMethods_AreDifferentFailures()
    {
        const string type = "System.NullReferenceException";
        const string message = "Object reference not set to an instance of an object.";

        var fromOrders = FailureFingerprint.Compute(type, message, type + ": " + message + "\n   at MyApp.Jobs.OrderJob.Run()");
        var fromInvoices = FailureFingerprint.Compute(type, message, type + ": " + message + "\n   at MyApp.Jobs.InvoiceJob.Run()");

        Assert.NotEqual(fromOrders.Fingerprint, fromInvoices.Fingerprint);
    }

    // ── Value, version and overloads ────────────────────────────────────────────────────

    /// <summary>
    /// Pins the exact v1 value. If this fails, a rule change altered existing fingerprints: bump
    /// <see cref="FailureFingerprint.VersionPrefix"/> (and this value) rather than changing the rules
    /// under the same version, or stored fingerprints will silently stop matching.
    /// </summary>
    [Fact]
    public void Golden_V1Value()
    {
        var result = FailureFingerprint.Compute(
            "Microsoft.Data.SqlClient.SqlException",
            "Transaction (Process ID 57) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.",
            "Microsoft.Data.SqlClient.SqlException (0x80131904): Transaction (Process ID 57) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.\n" +
            "   at Microsoft.Data.SqlClient.SqlConnection.OnError(SqlException exception, Boolean breakConnection, Action`1 wrapCloseInAction)\n" +
            "   at MyApp.Data.OrderRepository.<SaveAsync>d__4.MoveNext() in /src/MyApp/Data/OrderRepository.cs:line 57\n" +
            "   at MyApp.Jobs.OrderJob.RunAsync(Int32 orderId) in /src/MyApp/Jobs/OrderJob.cs:line 21");

        Assert.Equal("Transaction (Process ID <n>) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.", result.NormalizedMessage);
        Assert.Equal("MyApp.Data.OrderRepository.SaveAsync", result.TopFrame);
        Assert.Equal("v1:6eca44f1fc4dce28", result.Fingerprint);
    }

    [Fact]
    public void Fingerprint_HasTheCurrentVersionShape()
    {
        var fingerprint = FailureFingerprint.Compute("System.Exception", "Boom", null).Fingerprint;

        Assert.StartsWith(FailureFingerprint.VersionPrefix, fingerprint);
        Assert.Equal(FailureFingerprint.VersionPrefix.Length + 16, fingerprint.Length);
        Assert.True(FailureFingerprint.IsCurrentVersion(fingerprint));
    }

    [Theory]
    [InlineData("v1:0123456789abcdef", true)]
    [InlineData("v0:0123456789abcdef", false)]
    [InlineData("v2:0123456789abcdef", false)]
    [InlineData("v1:0123456789ABCDEF", false)]
    [InlineData("v1:0123456789abcde", false)]
    [InlineData("v1:0123456789abcdef0", false)]
    [InlineData("v1:0123456789abcdeg", false)]
    [InlineData("\"v1:0123456789abcdef\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCurrentVersion(string value, bool expected)
    {
        Assert.Equal(expected, FailureFingerprint.IsCurrentVersion(value));
    }

    [Fact]
    public void StateDataOverload_MatchesTheStringOverload()
    {
        var data = new Dictionary<string, string>
        {
            ["FailedAt"] = "2026-09-25T13:20:06.223Z",
            ["ExceptionType"] = "System.InvalidOperationException",
            ["ExceptionMessage"] = "Order 42 is locked",
            ["ExceptionDetails"] = "System.InvalidOperationException: Order 42 is locked\n   at MyApp.Jobs.OrderJob.Run()",
        };

        var fromData = FailureFingerprint.Compute(data);
        var fromStrings = FailureFingerprint.Compute(data["ExceptionType"], data["ExceptionMessage"], data["ExceptionDetails"]);

        Assert.Equal(fromStrings, fromData);
    }

    [Fact]
    public void StateDataOverload_RespectsTheDictionaryComparer()
    {
        // Storage connections hand state data back in case-insensitive dictionaries.
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["exceptiontype"] = "System.InvalidOperationException",
            ["exceptionmessage"] = "Boom",
        };

        Assert.Equal(
            FailureFingerprint.Compute("System.InvalidOperationException", "Boom", null),
            FailureFingerprint.Compute(data));
    }

    [Fact]
    public void NullsAndMissingKeys_CountAsEmpty()
    {
        var empty = FailureFingerprint.Compute(string.Empty, string.Empty, string.Empty);

        Assert.Equal(empty, FailureFingerprint.Compute(null, null, null));
        Assert.Equal(empty, FailureFingerprint.Compute((IDictionary<string, string>)null));
        Assert.Equal(empty, FailureFingerprint.Compute(new Dictionary<string, string>()));
    }

    [Property(MaxTest = 300)]
    public Property Compute_NeverThrows_AndAlwaysReturnsACurrentFingerprint(string type, string message, string details, string jobTypeName)
    {
        var result = FailureFingerprint.Compute(type, message, details, jobTypeName);

        return (FailureFingerprint.IsCurrentVersion(result.Fingerprint)
                && !result.NormalizedMessage.Contains('\n')
                && !result.TopFrame.Contains('\n'))
            .ToProperty();
    }
}
