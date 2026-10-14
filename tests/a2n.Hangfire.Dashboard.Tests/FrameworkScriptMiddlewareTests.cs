using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Xunit;
using a2n.Hangfire.Dashboard.Middleware;

namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// On .NET 10 a host without a .razor file (or RequiresAspNetWebAssets) has no
/// _framework/blazor.web.js, and the package's props can't fix that (NuGet doesn't import package
/// props during restore). The net10.0 build carries a fallback copy; net8.0 and net9.0 don't need
/// one and don't carry it. The host's own copy always wins.
/// </summary>
public class FrameworkScriptMiddlewareTests
{
    [Fact]
    public void TheBundledCopy_IsInTheNet10BuildOnly()
    {
        var resources = typeof(FrameworkScriptMiddleware).Assembly.GetManifestResourceNames();
#if NET10_0_OR_GREATER
        Assert.Contains(FrameworkScriptMiddleware.BundledBlazorWebScriptResource, resources);
        Assert.True(FrameworkScriptMiddleware.HasBundledBlazorWebScript);
        Assert.StartsWith("10.0.", FrameworkScriptMiddleware.BundledBlazorWebScriptVersion);
#else
        Assert.DoesNotContain(FrameworkScriptMiddleware.BundledBlazorWebScriptResource, resources);
        Assert.False(FrameworkScriptMiddleware.HasBundledBlazorWebScript);
        Assert.Null(FrameworkScriptMiddleware.BundledBlazorWebScriptVersion);
#endif
    }

    [Fact]
    public async Task TheHostsOwnCopy_IsServedFirst()
    {
        using var webRoot = new TempWebRoot("// the host's blazor.web.js");
        var (context, body) = Request("/_framework/blazor.web.js", webRoot.Provider);
        var nextCalled = false;
        var middleware = new FrameworkScriptMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("// the host's blazor.web.js", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task WithoutTheHostsCopy_OnlyNet10ServesTheBundledOne()
    {
        var (context, body) = Request("/_framework/blazor.web.js", new NullFileProvider());
        var nextCalled = false;
        var middleware = new FrameworkScriptMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

#if NET10_0_OR_GREATER
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/javascript", context.Response.ContentType);
        Assert.True(body.Length > 100_000, $"bundled script is only {body.Length} bytes");
        Assert.Equal(body.Length, context.Response.ContentLength);
        Assert.Contains("Blazor", Encoding.UTF8.GetString(body.ToArray()));
#else
        Assert.True(nextCalled);
        Assert.Equal(0, body.Length);
#endif
    }

    [Theory]
    [InlineData("/_framework/blazor.server.js")]
    [InlineData("/_framework/other.js")]
    public async Task TheBundledCopy_AnswersOnlyForBlazorWebJs(string path)
    {
        var (context, body) = Request(path, new NullFileProvider());
        var nextCalled = false;
        var middleware = new FrameworkScriptMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public void Startup_SaysNothingWhenTheHostServesTheScript()
    {
        using var webRoot = new TempWebRoot("// host");
        var logger = new CapturingLogger();

        FrameworkScriptMiddleware.ReportScriptSource(webRoot.Provider, logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Startup_SaysWhereTheScriptComesFrom_WhenTheHostHasNone()
    {
        var logger = new CapturingLogger();

        FrameworkScriptMiddleware.ReportScriptSource(new NullFileProvider(), logger);

        var (level, message) = Assert.Single(logger.Entries);
#if NET10_0_OR_GREATER
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("bundled copy", message);
        Assert.Contains("RequiresAspNetWebAssets", message);
#else
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("will render but not respond", message);
#endif
    }

    private static (DefaultHttpContext Context, MemoryStream Body) Request(string path, IFileProvider webRoot)
    {
        var services = new ServiceCollection()
            .AddSingleton<IWebHostEnvironment>(new FakeEnvironment(webRoot))
            .BuildServiceProvider();
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Response.Body = body;
        return (context, body);
    }

    private sealed class TempWebRoot : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hf-fsm-" + Guid.NewGuid().ToString("N"));

        public TempWebRoot(string script)
        {
            Directory.CreateDirectory(Path.Combine(_root, "_framework"));
            File.WriteAllText(Path.Combine(_root, "_framework", "blazor.web.js"), script);
            Provider = new PhysicalFileProvider(_root);
        }

        public PhysicalFileProvider Provider { get; }

        public void Dispose()
        {
            Provider.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeEnvironment(IFileProvider webRoot) : IWebHostEnvironment
    {
        public IFileProvider WebRootFileProvider { get; set; } = webRoot;
        public string WebRootPath { get; set; } = "";
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Production";
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
