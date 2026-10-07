using System.Reflection;

namespace Fserp.FleetOperations.Api.Hosting;

/// <summary>
/// The assemblies whose message handlers this host owns. Discovery is explicit: an assembly that is
/// not listed here is never scanned, and this host registers no catch-all handler or route policy.
/// Wolverine additionally discovers handlers in this host assembly itself, which this project owns;
/// Program.cs names it as <c>ApplicationAssembly</c> for that reason.
/// A handler class is discovered only when its name ends in <c>Handler</c> or <c>Consumer</c> (Wolverine's
/// convention). A message whose handler class is named otherwise has no route and is dropped.
/// </summary>
public static class HandlerAssemblies
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        // One line per bounded-context module, using that module's own AssemblyReference:
        //     Fserp.FleetOperations.Modules.Billing.AssemblyReference.Assembly,
        // A module that is not listed here has no handlers and no validators, however complete its
        // code looks. See src/Modules/README.md.
        Fserp.FleetOperations.Modules.Fleet.AssemblyReference.Assembly,
        Fserp.FleetOperations.Modules.Drivers.AssemblyReference.Assembly,
        Fserp.FleetOperations.Modules.Operations.AssemblyReference.Assembly,
        Fserp.FleetOperations.Modules.Administration.AssemblyReference.Assembly,
    ];
}
