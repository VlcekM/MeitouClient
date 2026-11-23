using Meitou.Simulation;
using Meitou.Simulation.Dialogue;

namespace Meitou.Game.Ui;

/// <summary>
/// A player reply an extension adds to a conversation (<see cref="UiRegistration.AddDialogueReplies"/>): its text, shown after the game's own
/// choices, and what choosing it does (on the host's thread; it changes the world only by queueing commands, and may open the mod's own window).
/// </summary>
public sealed record UiDialogueReply(string Text, Action Press);

/// <summary>
/// What an extension's dialogue replies get: the open conversation as the window shows it, the NPC talked to and the player's character (their
/// snapshots: record, faction, name; null when not in the snapshot), who says the current line, the snapshot, the tick and the command queue.
/// </summary>
public sealed class UiDialogueContext(ConversationView conversation, WorldSnapshot snapshot, Func<long> tick, Action<SimCommand> enqueue)
{
    /// <summary>
    /// The conversation: its NPC, player and speaker ids, the line, the game's choices and the log.
    /// </summary>
    public ConversationView Conversation { get; } = conversation;

    /// <summary>
    /// The NPC the player talks to (<see cref="CharacterSnapshot.RecordId"/>, <see cref="CharacterSnapshot.Faction"/>); null when not shown.
    /// </summary>
    public CharacterSnapshot? Npc { get; } = Find(snapshot, conversation.Npc);

    /// <summary>
    /// The player's character in the conversation; null when not shown.
    /// </summary>
    public CharacterSnapshot? Player { get; } = Find(snapshot, conversation.Player);

    /// <summary>
    /// Who says the current line (the NPC, or an interjector); null when not shown.
    /// </summary>
    public CharacterSnapshot? Speaker { get; } = Find(snapshot, conversation.Speaker.IsNone ? conversation.Npc : conversation.Speaker);

    /// <summary>
    /// The snapshot the window is laid out from (the one on screen).
    /// </summary>
    public WorldSnapshot Snapshot { get; } = snapshot;

    /// <summary>
    /// The world's tick now, for a command's <see cref="SimCommand.Tick"/>.
    /// </summary>
    public long Tick => tick();

    /// <summary>
    /// Queues a command for the world.
    /// </summary>
    public void Enqueue(SimCommand command) => enqueue(command);

    /// <summary>
    /// Ends the conversation (queues <see cref="EndConversation"/>), as closing the window does.
    /// </summary>
    public void Leave() => enqueue(new EndConversation { Tick = tick() });

    /// <summary>
    /// The snapshot of <paramref name="id"/>; null for none or one not in the snapshot.
    /// </summary>
    static CharacterSnapshot? Find(WorldSnapshot snapshot, CharacterId id) =>
        id.IsNone ? null : snapshot.Characters.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// The extensions' dialogue reply sources (docs/modding.md "Dialogue replies"), asked when a conversation's line or choices change.
/// </summary>
sealed class DialogueReplies
{
    /// <summary>
    /// The most replies the extensions together add to one line's choices.
    /// </summary>
    public const int MaxReplies = 4;

    /// <summary>
    /// The most characters a reply's text keeps.
    /// </summary>
    public const int MaxText = 200;

    /// <summary>
    /// The sources, with their owners, in registration order.
    /// </summary>
    readonly List<(string Owner, Func<UiDialogueContext, IEnumerable<UiDialogueReply>> Source)> _sources = [];

    /// <summary>
    /// Whether any extension adds replies.
    /// </summary>
    public bool Any => _sources.Count > 0;

    /// <summary>
    /// Adds <paramref name="owner"/>'s source.
    /// </summary>
    public void Add(string owner, Func<UiDialogueContext, IEnumerable<UiDialogueReply>> source) => _sources.Add((owner, source));

    /// <summary>
    /// Removes <paramref name="owner"/>'s sources.
    /// </summary>
    public void Remove(string owner) => _sources.RemoveAll(s => s.Owner == owner);

    /// <summary>
    /// The replies for <paramref name="context"/>, in registration order, at most <see cref="MaxReplies"/> of all sources together (a copy of the
    /// sources is walked). Texts are put on one line and cut to <see cref="MaxText"/>; presses are guarded by <paramref name="guard"/>. A source
    /// that throws, or gives a reply without a text or press, is reported once through <paramref name="report"/> and dropped.
    /// </summary>
    public IReadOnlyList<UiDialogueReply> For(UiDialogueContext context, CharacterPanelHooks.PressGuard guard, Action<UiDiagnostic>? report)
    {
        var replies = new List<UiDialogueReply>();
        foreach (var source in _sources.ToArray())
        {
            if (replies.Count >= MaxReplies) break;

            try
            {
                var added = source.Source(context)
                    .Take(MaxReplies - replies.Count)
                    .ToList();
                if (added.Any(r => r is null || string.IsNullOrWhiteSpace(r.Text) || r.Press is null))
                    throw new InvalidOperationException("A dialogue reply needs a text and a press.");

                replies.AddRange(added.Select(r => Shown(source.Owner, r, guard)));
            }
            catch (Exception e)
            {
                _sources.Remove(source);
                report?.Invoke(new UiDiagnostic(source.Owner, "dialogue replies (dropped)", e));
            }
        }

        return replies;
    }

    /// <summary>
    /// <paramref name="reply"/> as the window shows it: its text on one line and cut, its press guarded.
    /// </summary>
    static UiDialogueReply Shown(string owner, UiDialogueReply reply, CharacterPanelHooks.PressGuard guard)
    {
        string text = UiHost.Cut(reply.Text, MaxText)
            .ReplaceLineEndings(" ")
            .Trim();
        return new UiDialogueReply(text, guard(owner, "dialogue reply " + text, reply.Press));
    }
}
