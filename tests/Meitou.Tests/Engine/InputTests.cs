using Meitou.Game.Host.Input;

namespace Meitou.Tests.Engine;

public class InputTests
{
    readonly InputState _input = new();
    readonly InputBindings _bindings = new();

    ActionState Tick() => _bindings.Resolve(_input.Consume());

    [Fact]
    public void A_press_is_seen_by_exactly_one_of_several_ticks_in_a_frame()
    {
        _input.SetKey(Key.W, true);
        var t1 = Tick();
        var t2 = Tick();
        var t3 = Tick();
        Assert.True(t1.Pressed(InputAction.MoveForward));
        Assert.False(t2.Pressed(InputAction.MoveForward));
        Assert.False(t3.Pressed(InputAction.MoveForward));
        Assert.True(t1.Held(InputAction.MoveForward) && t2.Held(InputAction.MoveForward) && t3.Held(InputAction.MoveForward));
    }
}
