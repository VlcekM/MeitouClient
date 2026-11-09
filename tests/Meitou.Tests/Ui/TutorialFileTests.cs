using Meitou.Game;
using Meitou.Game.Tutorials;

namespace Meitou.Tests.Ui;

/// <summary>
/// <c>__tutorials.data</c> (docs/game/ui-screens.md section 7): the manager writes the active ids, then the pending ones, and reads them back.
/// </summary>
public sealed class TutorialFileTests
{
    [Fact]
    public void The_tutorial_file_keeps_active_pending_and_dismissed_tutorials()
    {
        string path = Path.Combine(Path.GetTempPath(), $"meitou-tutorials-{Guid.NewGuid():N}.data");
        try
        {
            var first = new TutorialManager(path);
            Assert.True(first.Fresh);
            Assert.True(first.Activate(5));
            Assert.True(first.Activate(3));
            first.Dismiss(3);
            first.Update(0, () => new TutorialPoll { EditorOrLoading = true, Pcs = [] });   // nothing triggers; the change is saved

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0xBAAAAAADu, BitConverter.ToUInt32(bytes, 0));
            Assert.Equal(34u, BitConverter.ToUInt32(bytes, 4));

            var second = new TutorialManager(path);
            Assert.False(second.Fresh);
            Assert.Equal([5], second.Active);
            Assert.False(second.Activate(5));   // already active
            Assert.True(second.Activate(3));    // dismissed, so it can come back
        }
        finally
        {
            File.Delete(path);
        }
    }
}
