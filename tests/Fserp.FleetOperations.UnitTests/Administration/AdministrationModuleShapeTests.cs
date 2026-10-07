using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Fserp.FleetOperations.Modules.Administration.Application;
using Fserp.FleetOperations.Modules.Administration.Application.Queries;
using Fserp.FleetOperations.Modules.Administration.Resources;
using Fserp.FleetOperations.UnitTests.Architecture;
using MPCore.Application.Messaging;
using MPCore.Audit;
using MPCore.Domain.Events;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Administration;

/// <summary>
/// The Administration twin of <c>DriversMessageCoverageTests</c>, plus the two things this module is
/// defined by its absence of: it writes nothing, and reading the trail is not itself audited.
/// </summary>
public sealed class AdministrationModuleShapeTests
{
    private static readonly Assembly Module =
        Fserp.FleetOperations.Modules.Administration.AssemblyReference.Assembly;

    private static readonly ResourceManager Texts = new(typeof(AdministrationMessages));

    private static IEnumerable<string> DeclaredCodes() =>
        typeof(AdministrationErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral
                && field.FieldType == typeof(string)
                && field.Name != nameof(AdministrationErrors.Domain))
            .Select(field => (string)field.GetRawConstantValue()!);

    private static IEnumerable<Type> Messages(Type marker) =>
        Module.GetTypes().Where(type =>
            type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == marker));

    private static MethodInfo HandlerOf(Type message) =>
        Assert.Single(
            Module.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            method => method.Name == "Handle" && method.GetParameters().FirstOrDefault()?.ParameterType == message);

    public static TheoryData<string> EveryCode()
    {
        var data = new TheoryData<string>();
        foreach (var code in DeclaredCodes())
        {
            data.Add(code);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCode))]
    public void Every_code_declared_in_AdministrationErrors_has_a_text(string code)
    {
        var key = AdministrationErrors.MessageKey(code);

        Assert.False(string.IsNullOrWhiteSpace(Texts.GetString(key, CultureInfo.InvariantCulture)), $"No text for {key}.");
    }

    [Fact]
    public void The_module_declares_exactly_the_one_code_its_validation_needs()
    {
        // docs/plans/administration.md names three validation checks and no business rule. Only one of
        // the three needs a code of this module's own: from < to. The enum allowlists and the page bound
        // are refused by FluentValidation's own codes.
        Assert.Equal(["AUDIT_RANGE_INVALID"], DeclaredCodes().Order());
    }

    [Fact]
    public void Keys_follow_the_module_convention()
    {
        Assert.Equal(
            "administration.audit_range_invalid",
            AdministrationErrors.MessageKey(AdministrationErrors.AuditRangeInvalid));
    }

    [Fact]
    public void The_error_domain_is_the_one_the_plan_names()
    {
        Assert.Equal("administration", AdministrationErrors.Domain);
    }

    [Fact]
    public void The_domain_folder_is_empty()
    {
        // docs/plans/administration.md: "The Domain/ folder stays empty. ... An append-only trail has no
        // invariant this module could break; nothing here writes." Measured on the folder, because an
        // aggregate added there would otherwise only be noticed by a reader.
        var domain = Path.Combine(
            Repository.ModulesDirectory,
            "Administration",
            "Fserp.FleetOperations.Modules.Administration",
            "Domain");

        Assert.True(Directory.Exists(domain), $"{domain} does not exist.");
        // The folder is kept by a .gitkeep placeholder, as the generator left it; what must stay empty
        // is the code in it.
        Assert.Empty(Directory.EnumerateFiles(domain, "*.cs", SearchOption.AllDirectories));
        Assert.Equal([".gitkeep"], Directory.EnumerateFileSystemEntries(domain).Select(Path.GetFileName));
    }

    [Fact]
    public void The_module_declares_no_business_rule_no_aggregate_and_no_domain_event()
    {
        // The compiled twin of the folder check above: nothing in this assembly can break an invariant,
        // raise an event or be saved.
        var types = Module.GetTypes();

        Assert.DoesNotContain(types, type => typeof(BusinessRule).IsAssignableFrom(type) && !type.IsAbstract);
        Assert.DoesNotContain(types, type => typeof(IDomainEvent).IsAssignableFrom(type) && !type.IsAbstract);
        Assert.DoesNotContain(types, type =>
            type.BaseType is { IsGenericType: true } baseType
            && baseType.GetGenericTypeDefinition().Name.StartsWith("AggregateRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void The_module_has_one_query_and_no_command()
    {
        // The census of the Administration message surface (docs/plans/administration.md: "Commands:
        // None."). A command here would mean something writes.
        Assert.Empty(Messages(typeof(ICommand<>)));
        Assert.Equal(["GetAuditEntries"], Messages(typeof(IQuery<>)).Select(type => type.Name).Order());
    }

    [Fact]
    public void The_query_handler_takes_the_audit_port_and_its_cancellation_token_and_nothing_else()
    {
        // Lead decision L-29, checked on the signature so a fourth port cannot appear quietly.
        var parameters = HandlerOf(typeof(GetAuditEntries)).GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.Equal([typeof(GetAuditEntries), typeof(IAuditQuery), typeof(CancellationToken)], parameters);
        Assert.DoesNotContain(typeof(IUnitOfWork), parameters);
    }

    [Fact]
    public void Nothing_in_the_module_takes_the_business_audit_recorder()
    {
        // AD-2: reading the trail is not itself audited. Asserted on every method in the assembly, not
        // only on the handler, so no helper can record a read either.
        var offending = Module.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(IBusinessAuditRecorder)
                || parameter.ParameterType == typeof(IAuditSink))
            .Select(parameter => $"{parameter.Member.DeclaringType?.Name}.{parameter.Member.Name} takes {parameter.ParameterType.Name}")
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void Nothing_in_the_module_takes_a_cache_port()
    {
        // "Cache candidates: None. The trail must be current when an administrator investigates." The
        // module does not even reference MPCore.Caching, so a cache port cannot appear without the
        // project file changing first.
        Assert.DoesNotContain(
            Module.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("MPCore.Caching", StringComparison.Ordinal) == true);

        var cacheLike = new Regex("Cache", RegexOptions.IgnoreCase);
        var offending = Module.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => cacheLike.IsMatch(parameter.ParameterType.Name))
            .Select(parameter => $"{parameter.Member.DeclaringType?.Name}.{parameter.Member.Name} takes {parameter.ParameterType.Name}")
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void The_query_carries_no_caller_identity_only_the_actor_it_filters_on()
    {
        // The other modules assert that no message has an actor-like property at all. This module is the
        // one exception and it is deliberate: the trail records who acted, so "show me what this subject
        // did" is a filter. The distinction that matters is that the value comes from the query string
        // and is never derived from the token or a header — proved in AuditEntryEndpointContractTests.
        var actorLike = new Regex("actor|user|subject|caller|tenant|role", RegexOptions.IgnoreCase);

        var named = typeof(GetAuditEntries).GetProperties()
            .Where(property => actorLike.IsMatch(property.Name))
            .Select(property => property.Name)
            .ToList();

        Assert.Equal([nameof(GetAuditEntries.ActorSubjectId)], named);
    }

    [Fact]
    public void The_view_exposes_no_MPCore_audit_type()
    {
        // docs/plans/administration.md: the view is owned by this module "so the REST contract does not
        // change if the package type does". A property typed AuditEntry, AuditActor, AuditCategory,
        // AuditOutcome or AuditFailure would put the package back on the wire.
        var views = Module.GetTypes()
            .Where(type => type.Namespace?.EndsWith(".Application.Views", StringComparison.Ordinal) == true)
            .ToList();
        Assert.NotEmpty(views);

        var offending = views
            .SelectMany(type => type.GetProperties().Select(property => (type, property)))
            .Where(item => item.property.PropertyType.Assembly == typeof(AuditEntry).Assembly)
            .Select(item => $"{item.type.Name}.{item.property.Name} is {item.property.PropertyType.Name}")
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }
}
