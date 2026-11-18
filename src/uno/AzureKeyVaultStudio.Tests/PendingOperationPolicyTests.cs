namespace AzureKeyVaultStudio.Tests;

public class PendingOperationPolicyTests
{
    private static readonly DateTimeOffset T1 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PendingOperation Op(
        PendingOperationKind kind,
        bool expectedExists = true,
        DateTimeOffset? expectedUpdatedOn = null,
        bool force = false,
        long id = 1,
        string name = "a",
        PendingOperationStatus status = PendingOperationStatus.Pending,
        DateTimeOffset? nextAttemptOn = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            ItemType = KeyVaultItemType.Secret,
            Name = name,
            ExpectedExists = expectedExists,
            ExpectedUpdatedOn = expectedUpdatedOn,
            Force = force,
            Status = status,
            NextAttemptOn = nextAttemptOn,
        };

    [Fact]
    public void Create_AppliesWhenAbsent_ConflictsWhenSomeoneElseCreatedIt()
    {
        var create = Op(PendingOperationKind.SetSecret, expectedExists: false);

        Assert.Equal(PendingPushDecision.Apply, PendingOperationPolicy.Evaluate(create, null));
        Assert.Equal(PendingPushDecision.Conflict, PendingOperationPolicy.Evaluate(create, new RemoteItemState(T1)));
    }

    [Fact]
    public void Update_AppliesWhenUnchanged_ToleratingSubSecondDifferences()
    {
        var update = Op(PendingOperationKind.SetSecret, expectedUpdatedOn: T1);

        Assert.Equal(PendingPushDecision.Apply, PendingOperationPolicy.Evaluate(update, new RemoteItemState(T1.AddMilliseconds(400))));
    }

    [Fact]
    public void Update_ConflictsWhenChangedOrDeletedRemotely()
    {
        var update = Op(PendingOperationKind.SetSecret, expectedUpdatedOn: T1);

        Assert.Equal(PendingPushDecision.Conflict, PendingOperationPolicy.Evaluate(update, new RemoteItemState(T1.AddMinutes(1))));
        Assert.Equal(PendingPushDecision.Conflict, PendingOperationPolicy.Evaluate(update, null));
    }

    [Fact]
    public void Update_WithoutExpectedTimestamp_Applies()
    {
        // e.g. a value change queued after a create that hasn't been pushed yet
        var update = Op(PendingOperationKind.SetSecret, expectedUpdatedOn: null);

        Assert.Equal(PendingPushDecision.Apply, PendingOperationPolicy.Evaluate(update, new RemoteItemState(T1)));
    }

    [Fact]
    public void Delete_IsAlreadyAppliedWhenItemIsGone()
    {
        Assert.Equal(PendingPushDecision.AlreadyApplied, PendingOperationPolicy.Evaluate(Op(PendingOperationKind.Delete, expectedUpdatedOn: T1), null));
    }

    [Fact]
    public void Delete_ConflictsWhenChangedRemotely()
    {
        Assert.Equal(PendingPushDecision.Conflict, PendingOperationPolicy.Evaluate(Op(PendingOperationKind.Delete, expectedUpdatedOn: T1), new RemoteItemState(T1.AddMinutes(1))));
    }

    [Theory]
    [InlineData(PendingOperationKind.SetSecret)]
    [InlineData(PendingOperationKind.Delete)]
    public void Forced_AppliesDespiteRemoteChanges(PendingOperationKind kind)
    {
        var op = Op(kind, expectedUpdatedOn: T1, force: true);

        Assert.Equal(PendingPushDecision.Apply, PendingOperationPolicy.Evaluate(op, new RemoteItemState(T1.AddMinutes(1))));
    }

    [Fact]
    public void SelectRunnable_HoldsBackLaterOperationsForBlockedItems()
    {
        var now = T1;
        var ops = new[]
        {
            Op(PendingOperationKind.SetSecret, id: 1, name: "a", status: PendingOperationStatus.Conflict),
            Op(PendingOperationKind.Delete, id: 2, name: "A"),
            Op(PendingOperationKind.SetSecret, id: 3, name: "b", nextAttemptOn: now.AddMinutes(5)),
            Op(PendingOperationKind.Delete, id: 4, name: "b"),
            Op(PendingOperationKind.SetSecret, id: 5, name: "c"),
        };

        Assert.Equal([5L], PendingOperationPolicy.SelectRunnable(ops, now, ignoreSchedule: false).Select(o => o.Id));
        Assert.Equal([3L, 4L, 5L], PendingOperationPolicy.SelectRunnable(ops, now, ignoreSchedule: true).Select(o => o.Id));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(50, 1800)]
    public void RetryDelay_DoublesAndIsCapped(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PendingOperationPolicy.RetryDelay(attempts));
    }
}
