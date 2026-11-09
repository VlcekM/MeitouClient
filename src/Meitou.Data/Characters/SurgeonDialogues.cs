using System.Collections.Concurrent;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// Plastic surgeons (docs/characters.md "Plastic surgeons"): the NPCs whose dialogue has a line with the effect <c>DA_CHARACTER_EDITOR</c> (11), and
/// what that line costs (its <c>DA_TAKE_MONEY</c> (8) effect's val0). The surgeon itself is talked to (DA 11 through the dialogue, paid by its line);
/// this check stands behind an unpaid <c>EditCharacter</c> (tests, scripts): only lines whose path from their DIALOGUE is gated by nothing but
/// <c>DC_PLAYERMONEY</c> (2) or <c>DC_IS_RECRUITABLE</c> (77) count (the Thieves Guild's rank-gated and Grey's escaped-slave lines are left out).
/// </summary>
public sealed class SurgeonDialogues(GameDatabase db)
{
    /// <summary>
    /// <c>DialogActionEnum</c> numbers (FCS editor): take money, character editor.
    /// </summary>
    public const int TakeMoney = 8, CharacterEditor = 11;

    /// <summary>
    /// <c>DialogConditionEnum</c> numbers the offer accepts: the player's money, the speaker recruitable.
    /// </summary>
    public const int PlayerMoney = 2, IsRecruitable = 77;

    /// <summary>
    /// The cost of each DIALOGUE by string id (<see cref="Cost"/>); null when it offers no surgery.
    /// </summary>
    readonly ConcurrentDictionary<string, int?> _byDialogue = new(StringComparer.Ordinal);

    /// <summary>
    /// The cost of each (CHARACTER, squad template) pair (<see cref="CostOf"/>); the template is empty when not the leader.
    /// </summary>
    readonly ConcurrentDictionary<(string, string), int?> _byCharacter = new();

    /// <summary>
    /// The cheapest surgery a CHARACTER offers through its own <c>dialogue</c>, its <c>dialogue package</c>s' <c>dialogs</c>, or (as its squad's
    /// leader) the squad template's <c>dialog leader</c> package; null when it offers none.
    /// </summary>
    public int? CostOf(string characterId, string? squadTemplateId, bool leader) =>
        _byCharacter.GetOrAdd((characterId, leader ? squadTemplateId ?? "" : ""), key => Find(key.Item1, key.Item2));

    /// <summary>
    /// The cheapest surgery of the character's dialogues, its packages' and, given a template, the template's <c>dialog leader</c> packages'.
    /// </summary>
    int? Find(string characterId, string templateId)
    {
        if (db.Find(characterId) is not { Type: FcsRecordType.Character } character) return null;
        var dialogues = new List<string>();
        dialogues.AddRange(character.GetReferences("dialogue").Select(r => r.TargetStringId));
        foreach (var package in character.GetReferences("dialogue package").Select(r => db.Find(r.TargetStringId)))
            if (package is not null) dialogues.AddRange(package.GetReferences("dialogs").Select(r => r.TargetStringId));
        if (templateId.Length > 0 && db.Find(templateId) is { } template)
            foreach (var package in template.GetReferences("dialog leader").Select(r => db.Find(r.TargetStringId)))
                if (package is not null)
                    dialogues.AddRange(package.GetReferences("dialogs").Select(r => r.TargetStringId));
        int? best = null;
        foreach (string d in dialogues.Distinct())
            if (_byDialogue.GetOrAdd(d, Cost) is { } cost && (best is null || cost < best)) best = cost;
        return best;
    }

    /// <summary>
    /// The cheapest open editor line of a DIALOGUE (its own conditions included), or null.
    /// </summary>
    int? Cost(string dialogueId)
    {
        if (db.Find(dialogueId) is not { Type: FcsRecordType.Dialogue } dialogue || !Open(dialogue)) return null;
        int? best = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(GameRecord node, int depth)
        {
            if (depth > 30 || !seen.Add(node.StringId)) return;
            if (node.Type == FcsRecordType.DialogueLine && !Open(node)) return;
            var effects = node.GetReferences("effects").Select(r => (Action: db.Find(r.TargetStringId)?.GetInt("action name") ?? -1,
                    r.Values.Value0)).ToList();
            if (effects.Any(e => e.Action == CharacterEditor))
            {
                int cost = effects.Where(e => e.Action == TakeMoney).Sum(e => e.Value0);
                if (best is null || cost < best) best = cost;
            }
            foreach (string list in new[] { "lines", "interrupt" })
                foreach (var r in node.GetReferences(list))
                    if (db.Find(r.TargetStringId) is { } next) Walk(next, depth + 1);
        }
        Walk(dialogue, 0);
        return best;
    }

    /// <summary>
    /// A line or dialogue the offer can pass: every condition is the player's money or the speaker being recruitable.
    /// </summary>
    bool Open(GameRecord node) =>
        node.GetReferences("conditions").All(r => db.Find(r.TargetStringId)?.GetInt("condition name") is PlayerMoney or IsRecruitable);
}