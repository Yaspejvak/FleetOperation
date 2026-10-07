using System.Reflection;
using System.Xml.Linq;
using Fserp.FleetOperations.Modules.Operations.Infrastructure.Persistence;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// The module boundary as it applies to Operations, now that Operations has real references to both
/// Contracts projects. <c>ModuleBoundaryTests.AllowedReferences</c> already declares the allowed pair;
/// these tests prove that row is genuinely exercised rather than vacuously true, and add the
/// assembly-level check that Operations' compiled types mention no Fleet or Drivers type outside those
/// two Contracts assemblies — the shape of Fleet's
/// <c>No_Fleet_domain_or_application_signature_mentions_an_infrastructure_type</c>.
/// </summary>
public sealed class OperationsBoundaryTests
{
    private const string Prefix = "Fserp.FleetOperations.Modules.";

    private static readonly Assembly Module = Fserp.FleetOperations.Modules.Operations.AssemblyReference.Assembly;

    private static readonly string[] AllowedForeignAssemblies =
    [
        Prefix + "Fleet.Contracts",
        Prefix + "Drivers.Contracts",
    ];

    private static string ProjectFile()
    {
        var root = FindRepositoryRoot();
        return Path.Combine(
            root, "src", "Modules", "Operations", Prefix + "Operations", Prefix + "Operations.csproj");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Fserp.FleetOperations.Backend.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_project_really_references_both_Contracts_projects()
    {
        // Without this, ModuleBoundaryTests' Operations row would pass on an empty reference list: Assert.All
        // over nothing succeeds. From this round on Operations actually uses both, so the row is load-bearing.
        var references = XDocument.Load(ProjectFile())
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order()
            .ToList();

        Assert.Equal([Prefix + "Drivers.Contracts", Prefix + "Fleet.Contracts"], references);
    }

    [Fact]
    public void The_compiled_module_binds_to_both_Contracts_assemblies_and_to_no_other_module()
    {
        // The compiler drops an unused reference from assembly metadata, so seeing both here is evidence
        // that Operations genuinely calls into them — and seeing nothing else is the boundary itself.
        var moduleReferences = Module.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Order()
            .ToList();

        Assert.Equal([Prefix + "Drivers.Contracts", Prefix + "Fleet.Contracts"], moduleReferences);
    }

    [Fact]
    public void No_Operations_type_signature_mentions_a_Fleet_or_Drivers_type_outside_the_two_Contracts_assemblies()
    {
        // The assembly-level twin of Fleet's infrastructure-type test. A Fleet Domain type reaching an
        // Operations signature — Capacity, Vehicle, OperationalStatus — fails here even if the project
        // reference that made it possible were added by accident.
        var types = Module.GetTypes();
        Assert.NotEmpty(types);

        var offending = types
            .SelectMany(type => SignatureTypes(type).Select(used => (type, used)))
            .Where(item => IsForeignModuleType(item.used))
            .Select(item => $"{item.type.FullName} uses {item.used.FullName} from {item.used.Assembly.GetName().Name}")
            .Distinct()
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void The_four_Contracts_ports_the_plan_names_appear_in_Operations_signatures()
    {
        // The positive half: the test above would also pass if Operations used nothing at all. These are
        // the four ports docs/plans/operations.md, "Dependencies", names, each a handler parameter.
        var used = Module.GetTypes()
            .SelectMany(SignatureTypes)
            .Where(type => AllowedForeignAssemblies.Contains(type.Assembly.GetName().Name, StringComparer.Ordinal))
            .Select(type => type.Name)
            .Distinct()
            .ToList();

        Assert.Contains("IVehicleAvailabilityReader", used);
        Assert.Contains("IVehicleCommitments", used);
        Assert.Contains("IDriverEligibilityReader", used);
        Assert.Contains("IDriverCommitments", used);
    }

    [Fact]
    public void VehicleType_crosses_the_boundary_as_a_value_and_never_in_an_Operations_signature()
    {
        // The fifth dependency the plan names behaves differently from the four ports, and the difference
        // is worth pinning rather than glossing: VehicleType is read off the vehicle snapshot inside
        // AssignMission's body and handed straight to IDriverCommitments. It is therefore absent from
        // every Operations member signature — which is exactly right, because no Operations type should
        // declare a Fleet type. The behaviour it serves is covered by
        // AssignMissionTests.The_vehicle_snapshot_supplies_the_type_the_driver_commitment_checks.
        var inSignatures = Module.GetTypes()
            .SelectMany(SignatureTypes)
            .Any(type => type == typeof(Fserp.FleetOperations.Modules.Fleet.Contracts.VehicleType));

        Assert.False(inSignatures);
        // It is still a real compile-time dependency: Fleet.Contracts is bound, and the only Fleet type
        // Operations may name outside the ports is this enum.
        Assert.Contains(
            Module.GetReferencedAssemblies().Select(reference => reference.Name),
            name => name == Prefix + "Fleet.Contracts");
    }

    [Fact]
    public void No_Fleet_or_Drivers_type_enters_the_Operations_Domain()
    {
        // docs/plans/operations.md on the aggregate: "ids only; no Fleet or Drivers type enters this
        // module's Domain". Checked on the Domain namespace alone, so a Contracts type used legitimately
        // in Application does not mask a leak into the model. VehicleType is a Contracts enum and is used
        // in Application only.
        var domainTypes = Module.GetTypes()
            .Where(type => type.Namespace?.Contains("Modules.Operations.Domain", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(domainTypes);

        var offending = domainTypes
            .SelectMany(type => SignatureTypes(type).Select(used => (type, used)))
            .Where(item => item.used.Assembly.GetName().Name?.StartsWith(Prefix, StringComparison.Ordinal) == true
                && item.used.Assembly != Module)
            .Select(item => $"{item.type.FullName} uses {item.used.FullName}")
            .Distinct()
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    private static bool IsForeignModuleType(Type type)
    {
        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly.StartsWith(Prefix, StringComparison.Ordinal)
            && assembly != Module.GetName().Name
            && !AllowedForeignAssemblies.Contains(assembly, StringComparer.Ordinal);
    }

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var direct = new List<Type>();
        if (type.BaseType is not null)
        {
            direct.Add(type.BaseType);
        }

        direct.AddRange(type.GetInterfaces());
        direct.AddRange(type.GetFields(all).Select(field => field.FieldType));
        direct.AddRange(type.GetProperties(all).Select(property => property.PropertyType));
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            direct.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
            if (method is MethodInfo info)
            {
                direct.Add(info.ReturnType);
            }
        }

        return direct.SelectMany(Expand);
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Expand(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }
}

/// <summary>
/// The mapping constants the host and the migration depend on. They are a contract between three places:
/// the EF model, the generated migration's index filter, and the host's unique-violation mapper.
/// </summary>
public sealed class MissionConfigurationTests
{
    [Fact]
    public void The_schema_and_table_are_the_ones_the_plan_names()
    {
        Assert.Equal("operations", MissionConfiguration.Schema);
        Assert.Equal("missions", MissionConfiguration.Table);
    }

    [Fact]
    public void The_two_index_names_are_the_ones_the_plan_names()
    {
        Assert.Equal("ux_missions_active_vehicle", MissionConfiguration.ActiveVehicleUniqueIndex);
        Assert.Equal("ux_missions_active_driver", MissionConfiguration.ActiveDriverUniqueIndex);
    }

    [Fact]
    public void The_index_predicate_reads_plainly_because_the_status_is_stored_as_a_string()
    {
        // The reason the plan gives for storing the status as a string. It is built from
        // MissionActivity.ResourceHoldingStatuses, so the predicate and the Domain's definition of
        // "conflicting" (O-8) cannot drift.
        Assert.Equal("status IN ('Assigned', 'InProgress')", MissionConfiguration.ResourceHoldingFilter);
        Assert.DoesNotContain("Scheduled", MissionConfiguration.ResourceHoldingFilter, StringComparison.Ordinal);
    }
}
