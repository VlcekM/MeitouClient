using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation.Dialogue;

namespace Meitou.Game.Ui;

/// <summary>
/// Conversations (docs/game/dialogue.md "UI", docs/simulation.md "Dialogue"), the same in both UI modes: while the world publishes a conversation the
/// original's conversation panel (<c>Kenshi_ConversationPanel</c>) is open, modal, so the game is paused as the original pauses it. The NPC panel
/// shows the speaker's name and line, the player panel the talker's name, the money and the choices (colours <c>DialogueTextOption</c> #b3b3b3,
/// <c>DialogueTextOptionHover</c> #00ff00 from <c>kenshi_colours.xml</c>); a click on a choice says it, Esc or closing leaves the conversation. The
/// portraits are the speaker's and the player character's, drawn into the last two cells of the portrait atlas.
/// </summary>
sealed class DialogueWindow(PlayerInterface player)
{
    /// <summary>
    /// The choices' text colour and their hover colour.
    /// </summary>
    static readonly Vector4 ChoiceColour = new(0xb3 / 255f, 0xb3 / 255f, 0xb3 / 255f, 1), ChoiceHover = new(0, 1, 0, 1);

    /// <summary>
    /// The conversation window is shown, and its end was sent.
    /// </summary>
    internal bool ConversationShown, ConversationEndSent;

    /// <summary>
    /// The last dialogue request handled. Kept by reference, not by id: a loaded or new world numbers its requests from 1 again, so an id seen in
    /// the world before would hide its first ones (the surgeon's editor after a load, #1047).
    /// </summary>
    DialogueRequest? _dialogueRequestHandled;

    /// <summary>
    /// The NPC line shown; a new one scrolls the box back to the top.
    /// </summary>
    string? _dialogueText;

    /// <summary>
    /// The choices' rows and the player panel grown to hold them.
    /// </summary>
    readonly DialogueChoiceLayout _choices = new();

    /// <summary>
    /// The code mods' replies shown after the game's choices, and the conversation view they were asked for (asked again only when its NPC, line
    /// or choices change, so the rows keep still while the player reads them).
    /// </summary>
    (ConversationView? For, IReadOnlyList<UiDialogueReply> Replies) _modReplies = (null, []);

    /// <summary>
    /// Opens the panel when a conversation opens, closes it when it ends, ends the conversation when the player closed the panel; runs a finished
    /// conversation's request.
    /// </summary>
    internal void SyncDialogueScreen()
    {
        if (player.FaithfulScreens is null) return;

        var snapshot = player.Session.CurrentSnapshot;
        var talk = snapshot.Conversation;
        if (talk is null)
        {
            if (ConversationShown && player.FaithfulScreens.IsOpen("dialogue")) player.FaithfulScreens.Close("dialogue");
            if (ConversationShown) player.UpdateMenuPause();
            ConversationShown = ConversationEndSent = false;
        }
        else if (!ConversationShown)
        {
            ConversationShown = true;
            ConversationEndSent = false;
            player.ContextMenuWindow.CloseContextMenu();
            player.FaithfulScreens.Open("dialogue");
            player.UpdateMenuPause();
        }
        else if (!player.FaithfulScreens.IsOpen("dialogue") && !ConversationEndSent)
        {
            player.Say(new EndConversation { Tick = player.Session.World.Tick });
            ConversationEndSent = true;
            player.UpdateMenuPause();
        }

        if (snapshot.DialogueRequest is { } request && !ReferenceEquals(request, _dialogueRequestHandled))
        {
            _dialogueRequestHandled = request;
            if (request.Kind == DialogueRequestKind.CharacterEditor)
                player.CharacterEditor.OpenEditor(Simulation.Editing.EditKind.Surgery, [request.Player], request.Npc, paid: true);
            else if (request.Kind == DialogueRequestKind.RecruitEditor)
                player.CharacterEditor.OpenEditor(Simulation.Editing.EditKind.Recruit, [request.Player]);
            else if (request.Kind == DialogueRequestKind.AnimalEditor)
                player.CharacterEditor.OpenEditor(Simulation.Editing.EditKind.Recruit, request.Characters);
        }
    }

    /// <summary>
    /// The dialogue window's bindings: the names, the NPC line (scrolled to the top when new) and the choices.
    /// </summary>
    internal void BindDialogueScreen(UiBindings b)
    {
        if (player.Session.CurrentSnapshot.Conversation is not { } talk) return;

        b.Text["NpcName"] = talk.SpeakerName;
        b.Text["PlayerName"] = talk.PlayerName;
        b.Text["NpcDialog"] = talk.Text;
        b.Properties["NpcDialog"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top" };

        // A line longer than the NPC box scrolls under the EditBox's own bar (the original grows the frame instead); each new line starts at the top.
        if (talk.Text != _dialogueText)
        {
            _dialogueText = talk.Text;
            player.FaithfulScreens.SetScroll("dialogue", "NpcDialog", 0);
        }

        // The choices are drawn as buttons, so the player box's template scroll bar would only be an empty track.
        var layout = player.FaithfulScreens.Layout("dialogue");
        _choices.Bind(b, layout, Choices(talk), player.UiPainter, player.Width, player.Height, player.StandardPixels);
        var playerBox = layout.Find("PlayerDialog");
        foreach (var bar in playerBox?.Children.Where(w => w.Type == "ScrollBar") ?? [])
            b.HiddenWidgets.Add(bar);
        b.NativeScroll.Add("PlayerDialog");   // the choices are drawn against the box, not scrolled by its canvas
        b.Text["MoneyLabelText"] = Loc.Tr("Money:");
        b.Text["MoneyAmountText"] = "c." + talk.PlayerMoney.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The choices, wrapped, down the player panel's dialog area.
    /// </summary>
    internal void AddDialogueChoices(List<UiControl> controls)
    {
        player.PortraitBar.AddDialoguePortraits(controls);
        if (player.Session.CurrentSnapshot.Conversation is not { } talk || controls.FirstOrDefault(c => c.Name == "PlayerDialog") is not { } area)
            return;

        _choices.AddChoices(controls, area, Choices(talk), ChoiceColour, ChoiceHover);
    }

    /// <summary>
    /// The choices shown for <paramref name="talk"/>: the game's, then the code mods' replies (<see cref="UiRegistration.AddDialogueReplies"/>).
    /// </summary>
    IReadOnlyList<string> Choices(ConversationView talk)
    {
        var replies = ModReplies(talk);
        return replies.Count == 0 ? talk.Choices : [.. talk.Choices, .. replies.Select(r => r.Text)];
    }

    /// <summary>
    /// The code mods' replies for <paramref name="talk"/>, asked again only when its NPC, line or choices changed; none without a mod that adds any.
    /// </summary>
    IReadOnlyList<UiDialogueReply> ModReplies(ConversationView talk)
    {
        if (!player.HasModDialogueReplies) return [];
        if (_modReplies.For is { } asked && SameLine(asked, talk)) return _modReplies.Replies;

        _modReplies = (talk, player.ModDialogueReplies(talk));
        return _modReplies.Replies;
    }

    /// <summary>
    /// Whether two views show the same line with the same choices to the same NPC.
    /// </summary>
    static bool SameLine(ConversationView a, ConversationView b) =>
        ReferenceEquals(a, b) || a.Npc == b.Npc && a.Player == b.Player && a.Text == b.Text && a.Choices.SequenceEqual(b.Choices);

    /// <summary>
    /// Picks choice <paramref name="index"/>: one of the game's goes to the world, a code mod's reply runs.
    /// </summary>
    void Choose(ConversationView talk, int index)
    {
        if (index < talk.Choices.Count)
        {
            player.Say(new DialogueChoice(index) { Tick = player.Session.World.Tick });
            return;
        }

        var replies = ModReplies(talk);
        if (index - talk.Choices.Count < replies.Count) replies[index - talk.Choices.Count].Press();
    }

    /// <summary>
    /// A choice picked: it goes to the world.
    /// </summary>
    internal void DialogueAction(MyGuiEvent e)
    {
        if (!e.Action.StartsWith("dialogue-choice:", StringComparison.Ordinal)) return;
        if (!int.TryParse(e.Action[16..], CultureInfo.InvariantCulture, out int index) || index < 0) return;

        if (player.Session.CurrentSnapshot.Conversation is { } talk) Choose(talk, index);
    }

    /// <summary>
    /// The number keys pick a choice while the panel is open (engine convenience).
    /// </summary>
    internal bool DialogueKey(Host.Input.Key key)
    {
        if (!player.FaithfulScreens.IsOpen("dialogue") || player.Session.CurrentSnapshot.Conversation is not { } talk) return false;

        int index = key - Host.Input.Key.D1;
        if (index < 0 || index > 8) return false;
        Choose(talk, index);
        return true;
    }
}