using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Fserp.FleetOperations.Modules.Drivers.Application.Ports;
using Fserp.FleetOperations.Modules.Drivers.Domain;
using Fserp.FleetOperations.Modules.Drivers.Resources;
using MPCore.Application.Messaging;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Drivers;

/// <summary>
/// The Drivers twin of <c>FleetMessageCoverageTests</c>: every message key the module uses has a text, so
/// a code added without one fails here rather than reaching a caller as a bare key.
/// </summary>
public sealed class DriversMessageCoverageTests
{
    private static readonly ResourceManager Texts = new(typeof(DriversMessages));

    private static IEnumerable<string> DeclaredCodes() =>
        typeof(DriversErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name != nameof(DriversErrors.Domain))
            .Select(field => (string)field.GetRawConstantValue()!);

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
    public void Every_code_declared_in_DriversErrors_has_a_text(string code)
    {
        var key = DriversErrors.MessageKey(code);

        Assert.False(string.IsNullOrWhiteSpace(Texts.GetString(key, CultureInfo.InvariantCulture)), $"No text for {key}.");
    }

    [Fact]
    public void The_module_declares_every_code_the_plan_names()
    {
        // The eight invariants of docs/plans/drivers.md plus the module's one failure descriptor. A code
        // the plan does not name cannot be added here without this list changing.
        Assert.Equal(
            [
                "DRIVER_HAS_MISSION_COMMITMENT", "DRIVER_NAME_REQUIRED", "DRIVER_NOT_ACTIVE",
                "DRIVER_NOT_AVAILABLE", "DRIVER_NOT_COMMITTED_TO_MISSION", "DRIVER_NOT_FOUND",
                "DRIVER_NOT_QUALIFIED", "DRIVER_QUALIFICATION_DUPLICATE", "DRIVER_QUALIFICATION_REQUIRED",
            ],
            DeclaredCodes().Order());
    }

    [Fact]
    public void Keys_follow_the_module_convention()
    {
        Assert.Equal(
            "drivers.driver_has_mission_commitment",
            DriversErrors.MessageKey(DriversErrors.HasMissionCommitment));
    }

    [Fact]
    public void Every_business_rule_of_the_module_uses_a_declared_code_its_own_domain_and_a_key_with_a_text()
    {
        var rules = Fserp.FleetOperations.Modules.Drivers.AssemblyReference.Assembly.GetTypes()
            .Where(type => typeof(BusinessRule).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();
        Assert.NotEmpty(rules);
        var declared = DeclaredCodes().ToHashSet(StringComparer.Ordinal);

        foreach (var type in rules)
        {
            var constructor = type.GetConstructors().Single();
            var rule = (BusinessRule)constructor.Invoke(constructor.GetParameters()
                .Select(parameter => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)
                .ToArray());

            Assert.Equal(DriversErrors.Domain, rule.ErrorDomain);
            Assert.Contains(rule.Code, declared);
            Assert.Equal(DriversErrors.MessageKey(rule.Code), rule.MessageKey);
            Assert.False(
                string.IsNullOrWhiteSpace(Texts.GetString(rule.MessageKey, CultureInfo.InvariantCulture)),
                $"No text for {rule.MessageKey}.");
        }
    }

    [Fact]
    public void The_module_has_a_rule_class_for_every_invariant_the_plan_names()
    {
        var codes = Fserp.FleetOperations.Modules.Drivers.AssemblyReference.Assembly.GetTypes()
            .Where(type => typeof(BusinessRule).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type =>
            {
                var constructor = type.GetConstructors().Single();
                return ((BusinessRule)constructor.Invoke(constructor.GetParameters()
                    .Select(parameter => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)
                    .ToArray())).Code;
            })
            .Order();

        // The eight rows of the plan's "Invariants" table. DRIVER_NOT_FOUND is absent on purpose: it is a
        // failure descriptor, not a broken rule.
        Assert.Equal(
            [
                "DRIVER_HAS_MISSION_COMMITMENT", "DRIVER_NAME_REQUIRED", "DRIVER_NOT_ACTIVE",
                "DRIVER_NOT_AVAILABLE", "DRIVER_NOT_COMMITTED_TO_MISSION", "DRIVER_NOT_QUALIFIED",
                "DRIVER_QUALIFICATION_DUPLICATE", "DRIVER_QUALIFICATION_REQUIRED",
            ],
            codes);
    }
}

/// <summary>
/// The Drivers twin of <c>FleetHandlerShapeTests</c>: the shape of every Drivers message and handler,
/// checked by reflection so a later handler is held to it too.
/// </summary>
public sealed class DriversHandlerShapeTests
{
    private static readonly Assembly Module = Fserp.FleetOperations.Modules.Drivers.AssemblyReference.Assembly;

    private static IEnumerable<Type> Messages(Type marker) =>
        Module.GetTypes().Where(type => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == marker));

    private static MethodInfo HandlerOf(Type message) =>
        Assert.Single(
            Module.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            method => method.Name == "Handle" && method.GetParameters().FirstOrDefault()?.ParameterType == message);

    [Fact]
    public void The_module_has_its_two_commands_and_two_queries()
    {
        // The census of the Drivers message surface (docs/plans/drivers.md). CommitToMission and
        // ReleaseFromMission are deliberately absent: they are not bus messages, they reach the aggregate
        // through IDriverCommitments inside the Operations transaction.
        Assert.Equal(
            ["ChangeDriverStatus", "RegisterDriver"],
            Messages(typeof(ICommand<>)).Select(type => type.Name).Order());
        Assert.Equal(
            ["GetAvailableDrivers", "GetDriver"],
            Messages(typeof(IQuery<>)).Select(type => type.Name).Order());
    }

    [Fact]
    public void No_command_or_query_carries_an_actor()
    {
        // CLAUDE.md: identity comes from the validated token, never from a message.
        var actorLike = new Regex("actor|user|subject|caller|tenant|role", RegexOptions.IgnoreCase);
        foreach (var message in Messages(typeof(ICommand<>)).Concat(Messages(typeof(IQuery<>))))
        {
            Assert.DoesNotContain(message.GetProperties(), property => actorLike.IsMatch(property.Name));
        }
    }

    [Fact]
    public void Every_query_handler_declares_no_unit_of_work_and_no_repository()
    {
        foreach (var query in Messages(typeof(IQuery<>)))
        {
            var parameters = HandlerOf(query).GetParameters().Select(parameter => parameter.ParameterType).ToList();
            Assert.DoesNotContain(typeof(IUnitOfWork), parameters);
            Assert.DoesNotContain(parameters, type => !type.IsGenericType
                && type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRepository<,>)));
        }
    }

    [Fact]
    public void Every_command_handler_declares_the_unit_of_work_and_takes_only_interfaces()
    {
        foreach (var command in Messages(typeof(ICommand<>)))
        {
            var ports = HandlerOf(command).GetParameters().Skip(1).Select(parameter => parameter.ParameterType).ToList();
            Assert.Contains(typeof(IUnitOfWork), ports);
            Assert.All(
                ports.Where(type => type != typeof(CancellationToken)),
                type => Assert.True(type.IsInterface, $"{command.Name} handler takes {type.Name}, which is not a port."));
        }
    }

    [Fact]
    public void Nothing_in_the_module_takes_a_cache_port()
    {
        // D-6 and the plan's query table ("Cached: no" for both). The module does not even reference
        // MPCore.Caching.Abstractions, so this is checked on the assembly's references as well as on the
        // handler signatures: a cache port cannot appear without the project file changing first.
        Assert.DoesNotContain(
            Module.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("MPCore.Caching", StringComparison.Ordinal) == true);

        var cacheLike = new Regex("Cache", RegexOptions.IgnoreCase);
        var offending = Module.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(method => method.GetParameters())
            .Where(parameter => cacheLike.IsMatch(parameter.ParameterType.Name))
            .Select(parameter => $"{parameter.Member.DeclaringType?.Name}.{parameter.Member.Name} takes {parameter.ParameterType.Name}")
            .ToList();

        Assert.True(offending.Count == 0, string.Join(Environment.NewLine, offending));
    }

    [Fact]
    public void The_module_has_no_event_handler_in_this_scope()
    {
        // docs/plans/drivers.md: "No handler in this scope (nothing is cached for drivers). An unrouted
        // event is dropped with an informational log; that is accepted and must not be counted as tested
        // behaviour." So no test here asserts delivery of the four driver events — and this one pins that
        // no handler class exists to deliver them to.
        Assert.DoesNotContain(
            Module.GetTypes(),
            type => type.Name.EndsWith("Handler", StringComparison.Ordinal)
                && type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Any(method => method.Name == "Handle"
                        && typeof(MPCore.Domain.Events.IDomainEvent).IsAssignableFrom(method.GetParameters().FirstOrDefault()?.ParameterType)));
    }

    [Fact]
    public void The_module_declares_the_four_domain_events_the_plan_names()
    {
        var events = Module.GetTypes()
            .Where(type => typeof(MPCore.Domain.Events.IDomainEvent).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => type.Name)
            .Order();

        Assert.Equal(
            ["DriverCommittedToMission", "DriverRegistered", "DriverReleasedFromMission", "DriverStatusChanged"],
            events);
    }
}
