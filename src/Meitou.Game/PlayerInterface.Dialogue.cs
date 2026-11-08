using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>
/// Conversations (docs/game/dialogue.md "UI", docs/simulation.md "Dialogue"), the same in both UI modes: while the world publishes a conversation the original's conversation panel
/// (<c>Kenshi_ConversationPanel</c>) is open, modal, so the game is paused as the original pauses it. The NPC panel shows the speaker's name and line, the player panel the talker's name,
/// the money and the choices (colours <c>DialogueTextOption</c> #b3b3b3, <c>DialogueTextOptionHover</c> #00ff00 from <c>kenshi_colours.xml</c>); a click on a choice says it, Esc or
/// closing leaves the conversation. The portraits are the speaker's and the player character's, drawn into the last two cells of the portrait atlas.
/// </summary>
sealed partial class PlayerInterface
{
    static readonly Vector4 ChoiceColour = new(0xb3 / 255f, 0xb3 / 255f, 0xb3 / 255f, 1), ChoiceHover = new(0, 1, 0, 1);
    bool conversationShown, conversationEndSent;
    long dialogueRequestHandled;
    string? dialogueText;

    /// <summary>Opens the panel when a conversation opens, closes it when it ends, ends the conversation when the player closed the panel; runs a finished conversation's request.</summary>
    void SyncDialogueScreen()
    {
        if (faithfulScreens is null) return;
        var snapshot = session.CurrentSnapshot;
        var talk = snapshot.Conversation;
        if (talk is null)
        {
            if (conversationShown && faithfulScreens.IsOpen("dialogue")) faithfulScreens.Close("dialogue");
            if (conversationShown) UpdateMenuPause();
            conversationShown = conversationEndSent = false;
        }
        else if (!conversationShown)
        {
            conversationShown = true;
            conversationEndSent = false;
            CloseContextMenu();
            faithfulScreens.Open("dialogue");
            UpdateMenuPause();
        }
        else if (!faithfulScreens.IsOpen("dialogue") && !conversationEndSent)
        {
            Say(new EndConversation { Tick = session.World.Tick });
            conversationEndSent = true;
            UpdateMenuPause();
        }
        if (snapshot.DialogueRequest is { } request && request.Id > dialogueRequestHandled)
        {
            dialogueRequestHandled = request.Id;
            if (request.Kind == DialogueRequestKind.CharacterEditor) OpenEditor(Meitou.Simulation.Editing.EditKind.Surgery, [request.Player], request.Npc, paid: true);
            else if (request.Kind == DialogueRequestKind.RecruitEditor) OpenEditor(Meitou.Simulation.Editing.EditKind.Recruit, [request.Player]);
            else if (request.Kind == DialogueRequestKind.AnimalEditor) OpenEditor(Meitou.Simulation.Editing.EditKind.Recruit, request.Characters);
        }
    }

    void BindDialogueScreen(UiBindings b)
    {
        if (session.CurrentSnapshot.Conversation is not { } talk) return;
        b.Text["NpcName"] = talk.SpeakerName;
        b.Text["PlayerName"] = talk.PlayerName;
        b.Text["NpcDialog"] = talk.Text;
        b.Properties["NpcDialog"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };
        // A line longer than the NPC box scrolls under the EditBox's own bar (the original grows the frame instead); each new line starts at the top.
        if (talk.Text != dialogueText) { dialogueText = talk.Text; faithfulScreens.SetScroll("dialogue", "NpcDialog", 0); }
        // The choices are drawn as buttons, so the player box's template scroll bar would only be an empty track.
        foreach (var bar in faithfulScreens.Layout("dialogue").Find("PlayerDialog")?.Children.Where(w => w.Type == "ScrollBar") ?? []) b.HiddenWidgets.Add(bar);
        b.NativeScroll.Add("PlayerDialog");   // the choices are drawn against the box, not scrolled by its canvas
        b.Text["MoneyLabelText"] = Loc.Tr("Money:");
        b.Text["MoneyAmountText"] = "c." + talk.PlayerMoney.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>The choices, one line each down the player panel's dialog area.</summary>
    void AddDialogueChoices(List<UiControl> controls)
    {
        AddDialoguePortraits(controls);
        if (session.CurrentSnapshot.Conversation is not { } talk || controls.FirstOrDefault(c => c.Name == "PlayerDialog") is not { } area) return;
        float line = MathF.Max(16, area.Bounds.Height / 7);
        for (int i = 0; i < talk.Choices.Count; i++)
        {
            var bounds = new UiRect(area.Bounds.X, area.Bounds.Y + i * line, area.Bounds.Width, line);
            controls.Add(new($"dialogue-choice-{i}", "Button", bounds, $"{i + 1}. {talk.Choices[i]}", $"dialogue-choice:{i}", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" }, Vector4.Zero,
                TextColour: ChoiceColour, HoverColour: ChoiceHover, Pickable: true, Clip: area.Clip ?? area.Bounds));
        }
    }

    void DialogueAction(MyGuiEvent e)
    {
        if (e.Action.StartsWith("dialogue-choice:", StringComparison.Ordinal) && int.TryParse(e.Action[16..], CultureInfo.InvariantCulture, out int index))
            Say(new DialogueChoice(index) { Tick = session.World.Tick });
    }

    /// <summary>The number keys pick a choice while the panel is open (engine convenience).</summary>
    bool DialogueKey(Meitou.Game.Host.Input.Key key)
    {
        if (!faithfulScreens.IsOpen("dialogue") || session.CurrentSnapshot.Conversation is not { } talk) return false;
        int index = key - Meitou.Game.Host.Input.Key.D1;
        if (index < 0 || index > 8) return false;
        if (index < talk.Choices.Count) Say(new DialogueChoice(index) { Tick = session.World.Tick });
        return true;
    }
}
