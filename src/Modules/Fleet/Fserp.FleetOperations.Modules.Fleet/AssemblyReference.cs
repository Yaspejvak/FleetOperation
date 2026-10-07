using System.Reflection;

namespace Fserp.FleetOperations.Modules.Fleet;

/// <summary>
/// Names this module's assembly for the host: handler and validator discovery
/// (<c>HandlerAssemblies.cs</c>) and EF Core mappings (<c>AppDbContext</c>).
/// </summary>
public static class AssemblyReference
{
    /// <summary>The Fleet module assembly.</summary>
    public static Assembly Assembly { get; } = typeof(AssemblyReference).Assembly;
}
