using Meitou.Simulation.Dialogue;

namespace Meitou.Tests.Simulation;

/// <summary>The censorship word list (<c>FUN_14067e8b0</c>, docs/game/dialogue.md "Censorship").</summary>
public class CensorshipTests
{
    [Theory]
    [InlineData("what a shitty, shitting day", "what a crappy, crapping day")]
    [InlineData("Shit and SHIT and shit", "Crap and CRAP and crap")]
    [InlineData("fucking Fuck FUCK", "buggering Bugger BUGGER")]
    [InlineData("Bollocks, bollocks, BOLLOCKS", "Damn, damn, DAMN")]
    [InlineData("asshole Asshole ASSHOLE asswipe", "idiot Idiot IDIOT idiot")]
    [InlineData("SHITTY ASSWIPE", "CRAPTY ASSWIPE")]
    public void Words_are_swapped_by_case(string text, string expected) => Assert.Equal(expected, Censorship.Apply(text));
}
