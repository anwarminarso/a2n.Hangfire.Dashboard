// This file exists only for standalone development/testing.
// In production, use SampleApp or your own host with:
//   builder.Services.AddHangfireAlternateDashboard();
//   app.UseHangfireAlternateDashboard();

using Hangfire;

var builder = WebApplication.CreateBuilder(args);

// Standalone mode requires Hangfire to be configured.
// Run SampleApp instead for a working demo.
builder.Services.AddHangfire(config => config
    .UseInMemoryStorage());

builder.Services.AddHangfireServer();
builder.Services.AddHangfireAlternateDashboard();

var app = builder.Build();

app.UseHangfireAlternateDashboard();

app.Run();
