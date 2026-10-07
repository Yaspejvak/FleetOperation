using System.Reflection;
using Fserp.FleetOperations.Modules.Operations.Domain;
using Fserp.FleetOperations.Modules.Operations.Domain.Events;
using MPCore.Domain.Rules;

namespace Fserp.FleetOperations.UnitTests.Operations;

/// <summary>
/// The <see cref="Mission"/> aggregate: what Create fixes, what each transition changes, and what each
/// one raises. The refusals are in <see cref="MissionTransitionMatrixTests"/>, which covers every edge
/// rather than the ones a happy path happens to touch.
/// </summary>
public sealed class MissionTests
{
    [Fact]
    public void A_created_mission_is_Draft_with_no_scheduled_time_and_no_resources()
    {
        var mission = Mission.Create(
            Guid.CreateVersion7(TestMissions.Now),
            Location.Create("Tehran"),
            Location.Create("Isfahan"),
            RequiredCapacity.FromKilograms(800m));

        Assert.Equal(MissionStatus.Draft, mission.Status);
        // O-1: the scheduled time is given at Schedule, not at Create.
        Assert.Null(mission.ScheduledAt);
        Assert.Null(mission.AssignedVehicleId);
        Assert.Null(mission.AssignedDriverId);
        Assert.Equal("Tehran", mission.Origin.Value);
        Assert.Equal("Isfahan", mission.Destination.Value);
        Assert.Equal(800m, mission.RequiredCapacity.Kilograms);
        Assert.IsType<MissionCreated>(Assert.Single(mission.DomainEvents));
    }

    [Fact]
    public void An_origin_may_equal_the_destination()
    {
        // O-7: a round trip is a mission, and nothing in the plan refuses it.
        var mission = Mission.Create(
            Guid.CreateVersion7(TestMissions.Now),
            Location.Create("Tehran"),
            Location.Create("Tehran"),
            RequiredCapacity.FromKilograms(1m));

        Assert.Equal(mission.Origin, mission.Destination);
    }

    [Fact]
    public void A_mission_needs_a_non_empty_identity()
    {
        var refused = Assert.Throws<ArgumentException>(() => Mission.Create(
            Guid.Empty,
            Location.Create("Tehran"),
            Location.Create("Isfahan"),
            RequiredCapacity.FromKilograms(1m)));

        Assert.Equal("id", refused.ParamName);
    }

    [Fact]
    public void Schedule_fixes_the_time_and_moves_the_mission_to_Scheduled()
    {
        var mission = TestMissions.Draft();

        mission.Schedule(TestMissions.ScheduledAt);

        Assert.Equal(MissionStatus.Scheduled, mission.Status);
        Assert.Equal(TestMissions.ScheduledAt, mission.ScheduledAt);
        var raised = Assert.IsType<MissionScheduled>(Assert.Single(mission.DomainEvents));
        Assert.Equal(TestMissions.ScheduledAt, raised.ScheduledAt);
    }

    [Fact]
    public void A_scheduled_time_in_the_past_is_accepted()
    {
        // O-2, decided default: the plan asked the question without proposing a rule, so there is none.
        // This test exists to make the absence deliberate: adding the rule later breaks it.
        var mission = TestMissions.Draft();
        var longAgo = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        mission.Schedule(longAgo);

        Assert.Equal(longAgo, mission.ScheduledAt);
        Assert.Equal(MissionStatus.Scheduled, mission.Status);
    }

    [Fact]
    public void The_scheduled_time_can_never_be_changed_because_Schedule_leaves_Draft_only()
    {
        // O-1. Schedule is the only writer of ScheduledAt, and its only legal source is Draft, which a
        // scheduled mission has left for good.
        var mission = TestMissions.Scheduled();
        var other = TestMissions.ScheduledAt.AddDays(3);

        var refused = Assert.Throws<BusinessRuleValidationException>(() => mission.Schedule(other));

        Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        Assert.Equal(TestMissions.ScheduledAt, mission.ScheduledAt);
    }

    [Fact]
    public void Assign_records_both_ids_and_moves_the_mission_to_Assigned()
    {
        var mission = TestMissions.Scheduled();
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();

        mission.Assign(vehicleId, driverId);

        Assert.Equal(MissionStatus.Assigned, mission.Status);
        Assert.Equal(vehicleId, mission.AssignedVehicleId);
        Assert.Equal(driverId, mission.AssignedDriverId);
        var raised = Assert.IsType<MissionAssigned>(Assert.Single(mission.DomainEvents));
        Assert.Equal(vehicleId, raised.VehicleId);
        Assert.Equal(driverId, raised.DriverId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Assign_refuses_an_empty_id(bool emptyVehicle)
    {
        var mission = TestMissions.Scheduled();

        var refused = Assert.Throws<ArgumentException>(() => mission.Assign(
            emptyVehicle ? Guid.Empty : Guid.CreateVersion7(),
            emptyVehicle ? Guid.CreateVersion7() : Guid.Empty));

        Assert.Equal(emptyVehicle ? "vehicleId" : "driverId", refused.ParamName);
        Assert.Equal(MissionStatus.Scheduled, mission.Status);
    }

    [Fact]
    public void Start_moves_an_assigned_mission_to_InProgress_and_keeps_both_resources()
    {
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        var mission = TestMissions.Assigned(vehicleId, driverId);

        mission.Start();

        Assert.Equal(MissionStatus.InProgress, mission.Status);
        // Start changes nothing in Fleet or Drivers; the ids stay on the mission.
        Assert.Equal(vehicleId, mission.AssignedVehicleId);
        Assert.Equal(driverId, mission.AssignedDriverId);
        Assert.IsType<MissionStarted>(Assert.Single(mission.DomainEvents));
    }

    [Fact]
    public void A_mission_may_start_before_its_scheduled_time()
    {
        // O-6, decided default. The aggregate reads no clock, so there is nothing to compare; this test
        // pins that absence.
        var mission = TestMissions.Draft();
        mission.Schedule(TestMissions.Now.AddYears(1));
        mission.Assign(Guid.CreateVersion7(), Guid.CreateVersion7());

        mission.Start();

        Assert.Equal(MissionStatus.InProgress, mission.Status);
    }

    [Fact]
    public void Complete_moves_an_in_progress_mission_to_Completed_and_keeps_the_record_of_who_carried_it()
    {
        var vehicleId = Guid.CreateVersion7();
        var driverId = Guid.CreateVersion7();
        var mission = TestMissions.InProgress(vehicleId, driverId);

        mission.Complete();

        Assert.Equal(MissionStatus.Completed, mission.Status);
        Assert.Equal(vehicleId, mission.AssignedVehicleId);
        Assert.Equal(driverId, mission.AssignedDriverId);
        Assert.IsType<MissionCompleted>(Assert.Single(mission.DomainEvents));
    }

    [Theory]
    [InlineData(MissionStatus.Draft)]
    [InlineData(MissionStatus.Scheduled)]
    [InlineData(MissionStatus.Assigned)]
    public void Cancel_reports_the_status_it_cancelled_from(MissionStatus from)
    {
        // The handler needs this to decide whether anything was committed (L-24): only Assigned held a
        // vehicle and a driver.
        var mission = TestMissions.In(from);

        var reported = mission.Cancel();

        Assert.Equal(from, reported);
        Assert.Equal(MissionStatus.Cancelled, mission.Status);
        var raised = Assert.IsType<MissionCancelled>(Assert.Single(mission.DomainEvents));
        Assert.Equal(from, raised.From);
    }

    [Fact]
    public void An_in_progress_mission_cannot_be_cancelled()
    {
        // O-3, decided default. Named on its own because it is the one Cancel source a reader might expect.
        var mission = TestMissions.InProgress(Guid.CreateVersion7(), Guid.CreateVersion7());

        var refused = Assert.Throws<BusinessRuleValidationException>(() => mission.Cancel());

        Assert.Equal("MISSION_INVALID_TRANSITION", refused.Rule.Code);
        Assert.Equal(MissionStatus.InProgress, mission.Status);
    }

    [Fact]
    public void Status_has_no_setter_outside_the_aggregate()
    {
        // "No method assigns Status from outside; there is no setter" (docs/plans/operations.md). The same
        // holds for every other state-carrying property: only the transition methods write them.
        var writable = typeof(Mission)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(writable);
    }
}

/// <summary>
/// Every edge of the state machine, legal and illegal, driven from the two enums rather than from a list
/// of cases someone remembered to write.
/// </summary>
/// <remarks>
/// The expected-legal set below is transcribed from the plan's diagram, independently of
/// <c>MissionStateMachine</c>. A seventh status, an extra transition or a changed edge in production code
/// therefore fails here instead of passing quietly.
/// </remarks>
public sealed class MissionTransitionMatrixTests
{
    /// <summary>The edges of docs/plans/operations.md, "State machine", transcribed by hand.</summary>
    private static readonly HashSet<(MissionStatus From, MissionTransition Transition)> LegalEdges =
    [
        (MissionStatus.Draft, MissionTransition.Schedule),
        (MissionStatus.Scheduled, MissionTransition.Assign),
        (MissionStatus.Assigned, MissionTransition.Start),
        (MissionStatus.InProgress, MissionTransition.Complete),
        (MissionStatus.Draft, MissionTransition.Cancel),
        (MissionStatus.Scheduled, MissionTransition.Cancel),
        (MissionStatus.Assigned, MissionTransition.Cancel),
    ];

    /// <summary>The code each transition refuses with: Assign is precondition 1, the rest are edges.</summary>
    private static string RefusalCodeOf(MissionTransition transition) =>
        transition == MissionTransition.Assign ? "MISSION_NOT_ASSIGNABLE" : "MISSION_INVALID_TRANSITION";

    public static TheoryData<MissionStatus, MissionTransition> EveryEdge()
    {
        var data = new TheoryData<MissionStatus, MissionTransition>();
        foreach (var status in Enum.GetValues<MissionStatus>())
        {
            foreach (var transition in Enum.GetValues<MissionTransition>())
            {
                data.Add(status, transition);
            }
        }

        return data;
    }

    private static void Invoke(Mission mission, MissionTransition transition)
    {
        switch (transition)
        {
            case MissionTransition.Schedule:
                mission.Schedule(TestMissions.ScheduledAt);
                break;
            case MissionTransition.Assign:
                mission.Assign(Guid.CreateVersion7(), Guid.CreateVersion7());
                break;
            case MissionTransition.Start:
                mission.Start();
                break;
            case MissionTransition.Complete:
                mission.Complete();
                break;
            case MissionTransition.Cancel:
                mission.Cancel();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition), transition, "Not a member.");
        }
    }

    [Theory]
    [MemberData(nameof(EveryEdge))]
    public void Every_edge_is_either_taken_or_refused_with_its_own_code(MissionStatus from, MissionTransition transition)
    {
        var mission = TestMissions.In(from);
        var legal = LegalEdges.Contains((from, transition));

        if (legal)
        {
            Invoke(mission, transition);

            Assert.Equal(MissionStateMachine.TargetOf(transition), mission.Status);
            Assert.Single(mission.DomainEvents);
            return;
        }

        var refused = Assert.Throws<BusinessRuleValidationException>(() => Invoke(mission, transition));

        Assert.Equal(RefusalCodeOf(transition), refused.Rule.Code);
        Assert.Equal("operations", refused.Rule.ErrorDomain);
        // The rule is checked before anything changes, so a refusal leaves the aggregate exactly as it was
        // and raises nothing.
        Assert.Equal(from, mission.Status);
        Assert.Empty(mission.DomainEvents);
    }

    [Fact]
    public void The_matrix_covers_every_status_and_every_transition()
    {
        // Guards the theory against shrinking silently: 6 statuses x 5 transitions.
        Assert.Equal(6, Enum.GetValues<MissionStatus>().Length);
        Assert.Equal(5, Enum.GetValues<MissionTransition>().Length);
        Assert.Equal(30, EveryEdge().Count);
    }

    [Fact]
    public void The_transcribed_diagram_and_the_production_table_agree()
    {
        // Two independent statements of the same seven edges; if either changes alone, this fails.
        foreach (var status in Enum.GetValues<MissionStatus>())
        {
            foreach (var transition in Enum.GetValues<MissionTransition>())
            {
                Assert.Equal(
                    LegalEdges.Contains((status, transition)),
                    MissionStateMachine.IsLegal(status, transition));
            }
        }
    }

    [Theory]
    [InlineData(MissionStatus.Completed)]
    [InlineData(MissionStatus.Cancelled)]
    public void A_terminal_status_has_no_outgoing_edge_at_all(MissionStatus terminal)
    {
        // Stated separately from the matrix because it is the property the plan names: "a terminal mission
        // can never return to an active state".
        Assert.All(
            Enum.GetValues<MissionTransition>(),
            transition => Assert.False(MissionStateMachine.IsLegal(terminal, transition)));
    }

    [Fact]
    public void Only_a_scheduled_mission_is_assignable()
    {
        // Assignment precondition 1, from the other side: the one status Assign accepts.
        var assignable = Enum.GetValues<MissionStatus>()
            .Where(status => MissionStateMachine.IsLegal(status, MissionTransition.Assign))
            .ToList();

        Assert.Equal([MissionStatus.Scheduled], assignable);
    }

    [Fact]
    public void Cancel_leaves_exactly_the_three_statuses_the_plan_names()
    {
        var cancellable = Enum.GetValues<MissionStatus>()
            .Where(status => MissionStateMachine.IsLegal(status, MissionTransition.Cancel))
            .ToList();

        Assert.Equal([MissionStatus.Draft, MissionStatus.Scheduled, MissionStatus.Assigned], cancellable);
    }
}

/// <summary>
/// The two status sets the module reasons about. They are deliberately different, and confusing them
/// would either hide active missions from a reader or let two missions hold one vehicle.
/// </summary>
public sealed class MissionActivityTests
{
    [Fact]
    public void Active_means_Scheduled_Assigned_and_InProgress()
    {
        // O-4, the owner's answer.
        Assert.Equal(
            [MissionStatus.Scheduled, MissionStatus.Assigned, MissionStatus.InProgress],
            MissionActivity.ActiveStatuses);
    }

    [Fact]
    public void Conflicting_means_Assigned_and_InProgress_only()
    {
        // O-8. A Scheduled mission holds no vehicle yet, so it is active for a reader but not for the
        // unique partial indexes.
        Assert.Equal([MissionStatus.Assigned, MissionStatus.InProgress], MissionActivity.ResourceHoldingStatuses);
        Assert.DoesNotContain(MissionStatus.Scheduled, MissionActivity.ResourceHoldingStatuses);
    }

    [Theory]
    [InlineData(MissionStatus.Draft, false)]
    [InlineData(MissionStatus.Scheduled, true)]
    [InlineData(MissionStatus.Assigned, true)]
    [InlineData(MissionStatus.InProgress, true)]
    [InlineData(MissionStatus.Completed, false)]
    [InlineData(MissionStatus.Cancelled, false)]
    public void The_compiled_specification_agrees_with_the_list(MissionStatus status, bool expected)
    {
        var mission = TestMissions.In(status);

        Assert.Equal(expected, MissionActivity.IsActive(mission));
        Assert.Equal(expected, MissionActivity.ActiveStatuses.Contains(status));
    }
}
