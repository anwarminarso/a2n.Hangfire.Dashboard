using System;
using System.Linq;
using System.Text.Json;
using Hangfire;
using Hangfire.InMemory;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using Xunit;

namespace a2n.Hangfire.Dashboard.Tests;

// Regression tests for the Job Builder "inherited method type" fix.
//
// When the operator selects/types a DERIVED type whose method is actually declared on a BASE type
// (an inherited, non-overridden method), the resolver returns a MethodInfo whose DeclaringType is
// the base. The job MUST nonetheless be constructed with the SELECTED type so that:
//   * class-level [Tag]/[Queue] attributes declared on the derived type still apply, and
//   * activation/display match the original Hangfire `AddOrUpdate<TDerived>(...)` semantics.
//
// These tests guard JobMethodResolver.ResolveMethod (ResolvedType = selected type) and the two
// HangfireMonitorService build sites (recurring + enqueue) that now use ResolvedType.

/// <summary>Base type declaring an ordinary public job method.</summary>
public class InheritedTypeBaseJob
{
    public void BaseProcess(string label) { }
}

/// <summary>
/// Derived type that does NOT redeclare <see cref="InheritedTypeBaseJob.BaseProcess"/>. Selecting
/// this type with that method is the divergence case the fix addresses.
/// </summary>
public sealed class InheritedTypeDerivedJob : InheritedTypeBaseJob
{
}

/// <summary>
/// An interface job contract (the portable, canonical target for a DI-dispatched job). Selecting
/// this in the Job Builder must resolve and build even though the method is abstract.
/// </summary>
public interface IInheritedTypeFtpContract
{
    void Transfer(string profile);
}

/// <summary>
/// An abstract-class job contract. Hangfire supports abstract job types exactly like interfaces
/// (the Job model only requires assignability; activation is via the DI JobActivator).
/// </summary>
public abstract class InheritedTypeAbstractFtpBase
{
    public abstract void Transfer(string profile);
}

public class InheritedMethodTypeTests
{
    private static readonly string DerivedTypeName = typeof(InheritedTypeDerivedJob).FullName!;
    private const string InheritedMethodName = nameof(InheritedTypeBaseJob.BaseProcess);

    private static HangfireMonitorService CreateService(JobStorage storage) =>
        new HangfireMonitorService(
            storage,
            null,
            new DashboardUIOptions
            {
                IsReadOnly = false,
                EnableJobManagement = true,
                AllowArbitraryMethodInvocation = true,
            },
            new JobMethodResolver());

    [Fact]
    public void ResolveMethod_InheritedMethod_ResolvedTypeIsSelectedDerivedType()
    {
        var resolver = new JobMethodResolver();
        var args = JsonDocument.Parse("[\"hello\"]").RootElement.EnumerateArray().ToList();

        var result = resolver.ResolveMethod(DerivedTypeName, InheritedMethodName, args.Count, args);

        Assert.True(result.Success, result.Error);
        // The method is physically declared on the base...
        Assert.Equal(typeof(InheritedTypeBaseJob), result.Method.DeclaringType);
        // ...but resolution reports the SELECTED (derived) type for job construction.
        Assert.Equal(typeof(InheritedTypeDerivedJob), result.ResolvedType);
    }

    [Fact]
    public void CreateOrUpdateRecurringJob_InheritedMethod_StoresSelectedDerivedType()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var result = service.CreateOrUpdateRecurringJob(new RecurringJobRequest(
            JobId: "inherited-recurring",
            TypeName: DerivedTypeName,
            MethodName: InheritedMethodName,
            ParameterJson: "[\"hello\"]",
            Cron: "* * * * *",
            Queue: "default",
            TimeZoneId: null,
            IsCustomMethod: true));

        Assert.True(result.Success, result.Error);

        var stored = Assert.Single(service.GetRecurringJobs());
        Assert.NotNull(stored.Job);
        // The stored job type is the SELECTED derived type, not the method's declaring base type.
        Assert.Equal(typeof(InheritedTypeDerivedJob), stored.Job.Type);
        Assert.Equal(InheritedMethodName, stored.Job.Method.Name);
        Assert.Equal(new object[] { "hello" }, stored.Job.Args.ToArray());
    }

    [Fact]
    public void EnqueueJob_InheritedMethod_StoresSelectedDerivedType()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var result = service.EnqueueJob(new EnqueueJobRequest(
            TypeName: DerivedTypeName,
            MethodName: InheritedMethodName,
            ParameterJson: "[\"hello\"]",
            Queue: "inherited-enqueue",
            IsCustomMethod: true));

        Assert.True(result.Success, result.Error);

        var details = storage.GetMonitoringApi().JobDetails(result.JobId);
        Assert.NotNull(details);
        // The enqueued job type is the SELECTED derived type, not the declaring base type.
        Assert.Equal(typeof(InheritedTypeDerivedJob), details.Job.Type);
        Assert.Equal(InheritedMethodName, details.Job.Method.Name);
    }

    // --- Abstract/interface contract targets (Hangfire supports both, activated via DI) ---------

    [Fact]
    public void ResolveMethod_InterfaceContract_ResolvesAbstractMethod()
    {
        var resolver = new JobMethodResolver();
        var args = JsonDocument.Parse("[\"primary\"]").RootElement.EnumerateArray().ToList();

        var result = resolver.ResolveMethod(
            typeof(IInheritedTypeFtpContract).FullName!, nameof(IInheritedTypeFtpContract.Transfer), args.Count, args);

        // The interface method is abstract but must still resolve — it's a valid DI-dispatched target.
        Assert.True(result.Success, result.Error);
        Assert.True(result.Method.IsAbstract);
        Assert.Equal(typeof(IInheritedTypeFtpContract), result.ResolvedType);
    }

    [Fact]
    public void CreateOrUpdateRecurringJob_InterfaceContract_StoresInterfaceType()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var result = service.CreateOrUpdateRecurringJob(new RecurringJobRequest(
            JobId: "iface-recurring",
            TypeName: typeof(IInheritedTypeFtpContract).FullName,
            MethodName: nameof(IInheritedTypeFtpContract.Transfer),
            ParameterJson: "[\"primary\"]",
            Cron: "* * * * *",
            Queue: "default",
            TimeZoneId: null,
            IsCustomMethod: false));

        Assert.True(result.Success, result.Error);

        var stored = Assert.Single(service.GetRecurringJobs());
        Assert.NotNull(stored.Job);
        // The job is stored against the INTERFACE — portable across servers, resolved via DI.
        Assert.Equal(typeof(IInheritedTypeFtpContract), stored.Job.Type);
    }

    [Fact]
    public void EnqueueJob_AbstractClassContract_StoresAbstractBaseType()
    {
        var storage = new InMemoryStorage();
        var service = CreateService(storage);

        var result = service.EnqueueJob(new EnqueueJobRequest(
            TypeName: typeof(InheritedTypeAbstractFtpBase).FullName,
            MethodName: nameof(InheritedTypeAbstractFtpBase.Transfer),
            ParameterJson: "[\"primary\"]",
            Queue: "abstract-contract",
            IsCustomMethod: true));

        // Hangfire supports abstract job types (parity with interfaces); the job stores the abstract
        // base type and would be activated via DI at run time.
        Assert.True(result.Success, result.Error);

        var details = storage.GetMonitoringApi().JobDetails(result.JobId);
        Assert.NotNull(details);
        Assert.Equal(typeof(InheritedTypeAbstractFtpBase), details.Job.Type);
    }
}
