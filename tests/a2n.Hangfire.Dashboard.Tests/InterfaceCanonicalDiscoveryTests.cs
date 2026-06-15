using System.Linq;
using Hangfire;
using Hangfire.Tags.Attributes;
using a2n.Hangfire.Dashboard;
using a2n.Hangfire.Dashboard.Services;
using Xunit;

namespace InterfaceCanonicalDiscoveryFixtures
{
    // A shared job contract declared as an interface, carrying the job's metadata attributes. This
    // is the portable, canonical identity for a DI-dispatched job whose concrete implementation may
    // differ (name/namespace) per server.
    public interface IIcd_FtpContract
    {
        [JobDisplayName("Icd contract transfer for {0}")]
        [Tag("icd")]
        void Transfer(string profile);
    }

    // A concrete implementation whose method is ALSO independently eligible ([Tag]). Under Option Y
    // it is surfaced as an Implementation (not hidden) so it can be targeted when DI is not used.
    public sealed class Icd_FtpImpl : IIcd_FtpContract
    {
        [Tag("icd")]
        public void Transfer(string profile) { }
    }

    // An abstract-class job contract (parallel to the interface, Req 5.11). The abstract method is
    // the canonical Contract; concrete subclass overrides are surfaced as Implementations.
    [Tag("icd-abstract")]
    public abstract class Icd_AbstractFtpBase
    {
        [JobDisplayName("Icd abstract transfer")]
        public abstract void Transfer(string profile);
    }

    // A server-specific concrete subclass overriding the abstract contract. Its override is
    // independently eligible (inherits the class [Tag]) and is surfaced as an Implementation.
    public sealed class Icd_RealFtp : Icd_AbstractFtpBase
    {
        public override void Transfer(string profile) { }
    }
}

namespace a2n.Hangfire.Dashboard.Tests
{
    // Req 5.11 (Option Y): discovery surfaces BOTH the interface/abstract Contract_Method and its
    // concrete implementations/overrides, each labelled with a JobMethodKind (Contract vs
    // Implementation), so operators can target the portable contract (DI-dispatched) or a concrete
    // implementation (when DI is not configured).
    public class InterfaceCanonicalDiscoveryTests
    {
        private static readonly IReadOnlyList<JobMethodDescriptor> Discovered =
            new JobMethodResolver().GetRegisteredMethods();

        private static readonly string InterfaceName =
            typeof(InterfaceCanonicalDiscoveryFixtures.IIcd_FtpContract).FullName!;

        private static readonly string ImplName =
            typeof(InterfaceCanonicalDiscoveryFixtures.Icd_FtpImpl).FullName!;

        private static readonly string AbstractBaseName =
            typeof(InterfaceCanonicalDiscoveryFixtures.Icd_AbstractFtpBase).FullName!;

        private static readonly string RealFtpName =
            typeof(InterfaceCanonicalDiscoveryFixtures.Icd_RealFtp).FullName!;

        [Fact]
        public void Discovery_SurfacesInterfaceJobMethod_AsContract()
        {
            var ifaceEntry = Discovered.SingleOrDefault(d =>
                d.TypeFullName == InterfaceName && d.MethodName == "Transfer");

            Assert.NotNull(ifaceEntry);
            // The interface contract's display name drives the label (the {0} placeholder is
            // formatted against an empty arg during discovery).
            Assert.StartsWith("Icd contract transfer for", ifaceEntry!.DisplayLabel);
            Assert.Equal(JobMethodKind.Contract, ifaceEntry.Kind);
        }

        [Fact]
        public void Discovery_SurfacesConcreteImplementation_AsImplementation()
        {
            // Option Y: the concrete implementation is NOT hidden — it is surfaced and labelled
            // Implementation, so an operator can target it when DI is not configured.
            var implEntry = Discovered.SingleOrDefault(d =>
                d.TypeFullName == ImplName && d.MethodName == "Transfer");

            Assert.NotNull(implEntry);
            Assert.Equal(JobMethodKind.Implementation, implEntry!.Kind);
        }

        [Fact]
        public void Discovery_SurfacesAbstractClassContractMethod_AsContract()
        {
            // Req 5.11: an eligible abstract method on an abstract class is a canonical Contract_Method.
            var contractEntry = Discovered.SingleOrDefault(d =>
                d.TypeFullName == AbstractBaseName && d.MethodName == "Transfer");

            Assert.NotNull(contractEntry);
            Assert.Equal(JobMethodKind.Contract, contractEntry!.Kind);
        }

        [Fact]
        public void Discovery_SurfacesConcreteOverride_AsImplementation()
        {
            // Option Y: the concrete subclass override is surfaced and labelled Implementation.
            var overrideEntry = Discovered.SingleOrDefault(d =>
                d.TypeFullName == RealFtpName && d.MethodName == "Transfer");

            Assert.NotNull(overrideEntry);
            Assert.Equal(JobMethodKind.Implementation, overrideEntry!.Kind);
        }
    }
}
