using Meitou.Rendering;

namespace Meitou.Tests.Characters;

public class CharacterDrawListLifetimeTests
{
    [Fact]
    public void Resource_disposal_notifies_each_owner_once_even_when_a_handler_reenters()
    {
        var list = new CharacterDrawList();
        var released = new List<string>();
        list.Disposing += () =>
        {
            released.Add("first");
            list.DisposeResources();
            list.Disposing += () => released.Add("added during disposal");
        };
        list.Disposing += () => released.Add("second");
        list.DisposeResources();
        list.DisposeResources();
        Assert.Equal(["first", "second"], released);
        Assert.Null(list.Context);
    }

    [Fact]
    public void A_failed_cleanup_does_not_skip_another_resource_owner_or_repeat_the_event()
    {
        var list = new CharacterDrawList();
        int attempted = 0, completed = 0;
        list.Disposing += () => { attempted++; throw new InvalidOperationException("synthetic resource cleanup failure"); };
        list.Disposing += () => completed++;
        list.DisposeResources();
        list.DisposeResources();
        Assert.Equal(1, attempted);
        Assert.Equal(1, completed);
        Assert.Null(list.Context);
    }
}
