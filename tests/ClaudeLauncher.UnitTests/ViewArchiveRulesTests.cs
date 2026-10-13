using ClaudeLauncher.Core.Catalog;
using ClaudeLauncher.Core.Contracts;

namespace ClaudeLauncher.UnitTests;

public sealed class ViewArchiveRulesTests
{
    private static SessionMetadata Row(
        Visibility visibility = Visibility.Active,
        long revision = 0,
        NativeArchiveState native = NativeArchiveState.Active) => new()
        {
            Key = new ConversationKey(ConversationKey.DesktopCode, "acc", "org", "s1"),
            Title = "示例会话",
            SearchKey = "示例会话",
            ProjectRef = @"G:\demo",
            SourceRevision = "1:1",
            Visibility = visibility,
            NativeArchiveState = native,
            LocalPinned = true,
            PreferenceRevision = revision,
        };

    [Fact]
    public void ArchiveOnlyChangesLocalVisibility()
    {
        var result = ViewArchiveRules.Archive(Row(), expectedRevision: 0, newRevision: 1);

        Assert.Equal(ViewArchiveOutcome.Applied, result.Outcome);
        Assert.Equal(Visibility.ArchivedView, result.Row.Visibility);
        // Claude's own observed state is untouched by our list preference.
        Assert.Equal(NativeArchiveState.Active, result.Row.NativeArchiveState);
    }

    [Fact]
    public void UndoPreservesPinAndProject()
    {
        var archived = ViewArchiveRules.Archive(Row(), 0, 1).Row;
        var undone = ViewArchiveRules.Undo(archived, Visibility.Active, 1, 2);

        Assert.Equal(ViewArchiveOutcome.Applied, undone.Outcome);
        Assert.Equal(Visibility.Active, undone.Row.Visibility);
        Assert.True(undone.Row.LocalPinned);
        Assert.Equal(@"G:\demo", undone.Row.ProjectRef);
    }

    [Fact]
    public void DoubleArchiveIsIdempotent()
    {
        var first = ViewArchiveRules.Archive(Row(), 0, 1);
        var second = ViewArchiveRules.Archive(first.Row, 1, 2);

        Assert.Equal(ViewArchiveOutcome.AlreadyApplied, second.Outcome);
        Assert.Equal(1, second.Revision);
    }

    [Fact]
    public void StaleRevisionCannotOverwriteNewerPreference()
    {
        var current = Row(revision: 7);
        var result = ViewArchiveRules.Archive(current, expectedRevision: 3, newRevision: 8);

        Assert.Equal(ViewArchiveOutcome.Conflict, result.Outcome);
        Assert.Equal(Visibility.Active, result.Row.Visibility);
    }

    [Fact]
    public void UndoAfterALaterChangeIsRefused()
    {
        // The row moved on after the operation we are trying to undo.
        var moved = Row(visibility: Visibility.ArchivedView, revision: 5);
        var undone = ViewArchiveRules.Undo(moved, Visibility.Active, revisionProducedByOperation: 1, newRevision: 6);

        Assert.Equal(ViewArchiveOutcome.Conflict, undone.Outcome);
    }

    [Fact]
    public void NativeStateRemainsUnknownWhenNotObserved()
    {
        var unobserved = Row(native: NativeArchiveState.Unknown);
        var result = ViewArchiveRules.Archive(unobserved, 0, 1);

        Assert.Equal(NativeArchiveState.Unknown, result.Row.NativeArchiveState);
    }
}
