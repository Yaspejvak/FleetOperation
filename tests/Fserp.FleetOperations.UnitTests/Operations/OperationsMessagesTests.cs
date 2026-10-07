using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Fserp.FleetOperations.Modules.Operations.Application.Ports;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Resources;
using MPCore.Application.Messaging;
using MPCore.Domain.Rules;
using MPCore.Persistence.Abstractions;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// The Operations twin of <c>FleetMessageCoverageTests</c> and <c>DriversMessageCoverageTests</c>: every
/// message key the module uses has a text, so a code added without one fails here rather than reaching a
/// caller as a bare key.
/// </summary>
public sealed class OperationsMessageCoverageTests
{
    private static readonly ResourceManager Texts = new(typeof(OperationsMessages));

    private static IEnumerable<string> DeclaredCodes() =>
        typeof(OperationsErrors)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name != nameof(OperationsErrors.Domain))
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
    public void Every_code_declared_in_OperationsErrors_has_a_text(string code)
    {
        var key = OperationsErrors.MessageKey(code);

        Assert.False(string.IsNullOrWhiteSpace(Texts.GetString(key, CultureInfo.InvariantCulture)), $"No text for {key}.");
    }

    [Fact]
    public void The_module_declares_every_code_the_plan_names_and_three_failure_descriptors()
    {
        // The four invariants of docs/plans/operations.md plus the three not-found failure descriptors the
        // handlers return. A code the plan does not name cannot be added without this list changing.
        Assert.Equal(
            [
                "MISSION_DRIVER_NOT_FOUND", "MISSION_INVALID_TRANSITION", "MISSION_LOCATION_REQUIRED",
                "MISSION_NOT_ASSIGNABLE", "MISSION_NOT_FOUND", "MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE",
                "MISSION_VEHICLE_NOT_FOUND",
            ],
            DeclaredCodes().Order());
    }

    [Fact]
    public void Keys_follow_the_module_convention()
    {
        Assert.Equal(
            "operations.mission_invalid_transition",
            OperationsErrors.MessageKey(OperationsErrors.InvalidTransition));
    }

    [Fact]
    public void Every_business_rule_of_the_module_uses_a_declared_code_its_own_domain_and_a_key_with_a_text()
    {
        var rules = Fserp.FleetOperations.Modules.Operations.AssemblyReference.Assembly.GetTypes()
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

            Assert.Equal(OperationsErrors.Domain, rule.ErrorDomain);
            Assert.Contains(rule.Code, declared);
            Assert.Equal(OperationsErrors.MessageKey(rule.Code), rule.MessageKey);
            Assert.False(
                string.IsNullOrWhiteSpace(Texts.GetString(rule.MessageKey, CultureInfo.InvariantCulture)),
                $"No text for {rule.MessageKey}.");
        }
    }

    [Fact]
    public void The_module_has_a_rule_class_for_every_invariant_the_plan_names()
    {
        var codes = Fserp.FleetOperations.Modules.Operations.AssemblyReference.Assembly.GetTypes()
            .Where(type => typeof(BusinessRule).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type =>
            {
                var constructor = type.GetConstructors().Single();
                return ((BusinessRule)constructor.Invoke(constructor.GetParameters()
                    .Select(parameter => parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null)
                    .ToArray())).Code;
            })
            .Order();

        // The four rows of the plan's "Invariants" table. The three *_NOT_FOUND codes are absent on
        // purpose: they are failure descriptors, not broken rules.
        Assert.Equal(
            [
                "MISSION_INVALID_TRANSITION", "MISSION_LOCATION_REQUIRED", "MISSION_NOT_ASSIGNABLE",
                "MISSION_REQUIRED_CAPACITY_MUST_BE_POSITIVE",
            ],
            codes);
    }

    [Fact]
    public void The_invalid_transition_rule_carries_both_arguments_the_plan_names()
    {
        // "arguments: current status, requested transition" (docs/plans/operations.md). They are what lets
        // the rendered message say where the mission is and what was asked of it.
        var rule = new Fserp.FleetOperations.Modules.Operations.Domain.Rules.MissionInvalidTransitionRule(
            MissionStatus.InProgress, MissionTransition.Cancel);

        Assert.Equal("InProgress", rule.MessageArguments["current_status"]);
        Assert.Equal("Cancel", rule.MessageArguments["requested_transition"]);
    }
}

/// <summary>
/// The Operations twin of <c>FleetHandlerShapeTests</c>: the shape of every Operations message and
/// handler, checked by reflection so a later handler is held to it too.
/// </summary>
public sealed class OperationsHandlerShapeTests
{
    private static readonly Assembly Module = Fserp.FleetOperations.Modules.Operations.AssemblyReference.Assembly;

    private static IEnumerable<Type> Messages(Type marker) =>
        Module.GetTypes().Where(type => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == marker));

    private static MethodInfo HandlerOf(Type message) =>
        Assert.Single(
            Module.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static)),
            method => method.Name == "Handle" && method.GetParameters().FirstOrDefault()?.ParameterType == message);

    [Fact]
    public void The_module_has_its_six_commands_and_two_queries()
    {
        // The census of the Operations message surface (docs/plans/operations.md, "Commands" and
        // "Queries"). A seventh command cannot appear without this list changing.
        Assert.Equal(
            ["AssignMission", "CancelMission", "CompleteMission", "CreateMission", "ScheduleMission", "StartMission"],
            Messages(typeof(ICommand<>)).Select(type => type.Name).Order());
        Assert.Equal(
            ["GetActiveMissions", "GetMission"],
            Messages(typeof(IQuery<>)).Select(type => type.Name).Order());
    }

    [Fact]
    public void Every_command_has_a_validator()
    {
        var validated = Module.GetTypes()
            .Where(type => type.BaseType?.IsGenericType == true
                && type.BaseType.GetGenericTypeDefinition() == typeof(FluentValidation.AbstractValidator<>))
            .Select(type => type.BaseType!.GetGenericArguments()[0])
            .ToHashSet();

        Assert.All(Messages(typeof(ICommand<>)), command => Assert.Contains(command, validated));
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
            Assert.DoesNotContain(typeof(IMissionRepository), parameters);
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
        // docs/plans/operations.md: the two queries are "Not cached", and nothing a command decides on may
        // ever be cached (decision 4). The module does not even reference MPCore.Caching.Abstractions, so
        // a cache port cannot appear without the project file changing first.
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
    public void The_module_declares_the_six_domain_events_the_plan_names()
    {
        var events = Module.GetTypes()
            .Where(type => typeof(MPCore.Domain.Events.IDomainEvent).IsAssignableFrom(type) && !type.IsAbstract)
            .Select(type => type.Name)
            .Order();

        Assert.Equal(
            [
                "MissionAssigned", "MissionCancelled", "MissionCompleted", "MissionCreated", "MissionScheduled",
                "MissionStarted",
            ],
            events);
    }

    [Fact]
    public void The_module_has_no_event_handler_in_this_scope()
    {
        // docs/plans/operations.md, "Domain events": "No handler in this scope; cache eviction is driven by
        // Fleet's own events, which the commit and release calls raise." This pins that no handler exists.
        Assert.DoesNotContain(
            Module.GetTypes(),
            type => type.Name.EndsWith("Handler", StringComparison.Ordinal)
                && type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Any(method => method.Name == "Handle"
                        && typeof(MPCore.Domain.Events.IDomainEvent).IsAssignableFrom(method.GetParameters().FirstOrDefault()?.ParameterType)));
    }

    [Fact]
    public void The_module_publishes_no_Contracts_project()
    {
        // The module map: "Operations | Mission: lifecycle and assignment | Publishes: nothing". Nobody
        // calls Operations, so there is no Contracts assembly to call it through.
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name),
            name => name == "Fserp.FleetOperations.Modules.Operations.Contracts");
    }
}
