using a2n.Hangfire.Dashboard;
using Hangfire;
using Hangfire.Dashboard;
using Xunit;
namespace a2n.Hangfire.Dashboard.Tests;

/// <summary>
/// Tests for AlternateDashboardOptions.
/// </summary>
public class AlternateDashboardOptionsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var options = new AlternateDashboardOptions();

        Assert.Equal("/", options.AppPath);
        Assert.Equal("Hangfire Dashboard", options.DashboardTitle);
        Assert.Equal(2000, options.StatsPollingInterval);
        Assert.False(options.IsReadOnly);
        Assert.Equal(20, options.DefaultRecordsPerPage);
        Assert.Equal("auto", options.DefaultTheme);
        Assert.Empty(options.Authorization);
    }

    [Fact]
    public void FromDashboardOptions_MapsTitle()
    {
        var hangfireOptions = new DashboardOptions
        {
            DashboardTitle = "My Custom Title"
        };

        var result = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);

        Assert.Equal("My Custom Title", result.DashboardTitle);
    }

    [Fact]
    public void FromDashboardOptions_MapsAppPath()
    {
        var hangfireOptions = new DashboardOptions
        {
            AppPath = "/my-app"
        };

        var result = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);

        Assert.Equal("/my-app", result.AppPath);
    }

    [Fact]
    public void FromDashboardOptions_MapsPollingInterval()
    {
        var hangfireOptions = new DashboardOptions
        {
            StatsPollingInterval = 5000
        };

        var result = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);

        Assert.Equal(5000, result.StatsPollingInterval);
    }

    [Fact]
    public void FromDashboardOptions_MapsRecordsPerPage()
    {
        var hangfireOptions = new DashboardOptions
        {
            DefaultRecordsPerPage = 50
        };

        var result = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);

        Assert.Equal(50, result.DefaultRecordsPerPage);
    }

    [Fact]
    public void FromDashboardOptions_DarkModeEnabled_SetsAutoTheme()
    {
        var hangfireOptions = new DashboardOptions
        {
            DarkModeEnabled = true
        };

        var result = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);

        Assert.Equal("auto", result.DefaultTheme);
    }

    [Fact]
    public void FromDashboardOptions_DarkModeDisabled_SetsLightTheme()
    {
        var hangfireOptions = new DashboardOptions
        {
            DarkModeEnabled = false
        };

        var result = AlternateDashboardOptions.FromDashboardOptions(hangfireOptions);

        Assert.Equal("light", result.DefaultTheme);
    }

    [Fact]
    public void FromDashboardOptions_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AlternateDashboardOptions.FromDashboardOptions(null!));
    }
}

/// <summary>
/// Tests for IAlternateDashboardAuthorizationFilter interface contract.
/// </summary>
public class AuthorizationFilterTests
{
    [Fact]
    public void AllowAllFilter_AuthorizesEveryone()
    {
        var filter = new AllowAllFilter();
        Assert.True(filter.Authorize(null!));
    }

    [Fact]
    public void DenyAllFilter_DeniesEveryone()
    {
        var filter = new DenyAllFilter();
        Assert.False(filter.Authorize(null!));
    }

    private class AllowAllFilter : IAlternateDashboardAuthorizationFilter
    {
        public bool Authorize(Microsoft.AspNetCore.Http.HttpContext context) => true;
    }

    private class DenyAllFilter : IAlternateDashboardAuthorizationFilter
    {
        public bool Authorize(Microsoft.AspNetCore.Http.HttpContext context) => false;
    }
}
