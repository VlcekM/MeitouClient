using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/PrisonerRecruitment.h/.cpp (Proving Grounds by Shutori, GPLv3), task 6.4a (PA §2.19; CAP 15): every
// function of PrisonerRecruitment.cpp and its anonymous-namespace helpers, over IPrisonerRecruitmentHost (the character, the arena state, the store
// and the recruit). The price is Core PrisonerRecruitmentLogic (task 1.x, PGConfig's recruitment values and Marks multiplier); the two-phase
// transaction Core RewardTransaction (CompleteAction). The glue that binds the host to a world is src/Game/PrisonerRecruitment.cs; the engine call
// is src/Engine/Recruiting.cs. The class is not named PrisonerRecruitmentLogic: that name is Core's port of PrisonerRecruitmentLogic.cpp.
//
// The C++ anonymous namespace (PrisonerRecruitment.cpp:32-52), helper by helper:
//   RecruitmentPayment (:34-45) ... this file (RecruitmentPayment): Binding + ReservedAccounting, Matches through IPrisonerRecruitmentHost.IdentityMatches
//   RecruitAction (:46-51) ........ this file (RecruitAction): Apply = IPrisonerRecruitmentHost.Recruit (ou->player->recruit(prisoner, false))

/// <summary>What PrisonerRecruitment.cpp reads and does through the engine and the other modules (the glue implements it over PgEngine,
/// PrisonerUtil, ArenaIngress, SparSession, LeaderboardStore, FighterIdentity and src/Engine/Recruiting.cs).</summary>
public interface IPrisonerRecruitmentHost<TId>
{
    /// <summary><c>!prisoner</c>: no character at all.</summary>
    bool IsNull(TId c);
    /// <summary><c>prisoner-&gt;isValid()</c>.</summary>
    bool Valid(TId c);
    /// <summary><c>prisoner-&gt;isDead()</c>.</summary>
    bool IsDead(TId c);
    /// <summary><c>prisoner-&gt;isUnconcious()</c>.</summary>
    bool IsUnconscious(TId c);
    /// <summary><c>prisoner-&gt;getStats()</c> is non-null (false: none) and its <c>getOverallSkillLevel_0_100()</c>.</summary>
    bool TryOverallSkill(TId c, out float skill);
    /// <summary><c>PrisonerUtil::IsRosterPrisoner(prisoner)</c>.</summary>
    bool IsRosterPrisoner(TId c);
    /// <summary><c>ArenaIngress::IsPending()</c>.</summary>
    bool IngressPending();
    /// <summary><c>SparSession::IsActive()</c>.</summary>
    bool SparActive();
    /// <summary><c>PrisonerUtil::HasRuntimeActivity()</c>.</summary>
    bool PrisonerRuntimeActivity();
    /// <summary><c>ou &amp;&amp; ou-&gt;player</c>: the player side a recruit joins exists.</summary>
    bool PlayerReady();
    /// <summary>Port only: the engine's recruit can be called (the Recruitment part is on: its members resolved). False = the part is off.</summary>
    bool RecruitAvailable();
    /// <summary><c>LeaderboardStore::GetMarks(prisoner)</c>.</summary>
    int GetMarks(TId c);
    /// <summary><c>LeaderboardStore::ReserveProgression(prisoner, purchase, accounting)</c>.</summary>
    bool ReserveProgression(TId c, ArenaLedger.Purchase purchase, RewardTransaction.ReservedAccounting accounting);
    /// <summary>RecruitmentPayment::Matches (:39-44): <c>actor.getCharacter() == prisoner &amp;&amp; FighterIdentity::Find(prisoner, current)
    /// &amp;&amp; current == identity</c>.</summary>
    bool IdentityMatches(TId c, string identity);
    /// <summary>RecruitAction::Apply (:50): <c>ou-&gt;player-&gt;recruit(prisoner, false)</c>. True when the prisoner joined the player's squad;
    /// false leaves it a prisoner (never throws: a throw is a refused write in the glue).</summary>
    bool Recruit(TId c);
    /// <summary>Port only (review fix): the character is a player character now (<c>CharacterCold.IsPlayer</c>): after an exception inside the
    /// recruit, whether the recruit happened.</summary>
    bool JoinedPlayer(TId c);
    /// <summary><c>PrisonerUtil::ForgetRosterPrisoner(prisoner)</c>.</summary>
    void ForgetRosterPrisoner(TId c);
    /// <summary><c>prisoner-&gt;getName()</c>.</summary>
    string Name(TId c);
    /// <summary><c>PGLog::Debug</c>.</summary>
    void Debug(string line);
    /// <summary>A Meitou error line (port only: an exception in a recruitment).</summary>
    void Error(string line);
}

/// <summary>PrisonerRecruitment.cpp's rules over a host (task 6.4a): the price of a prisoner's freedom, paid from its own Arena Marks through a ledger
/// reservation, and the recruit as the one irreversible action of <see cref="RewardTransaction.CompleteAction"/>. Every status text is the C++'s,
/// byte for byte, except the two port-only lines named in docs/divergences.md ("PrisonerRecruitment").</summary>
public sealed class PrisonerRecruitmentGlue<TId>
{
    readonly IPrisonerRecruitmentHost<TId> h;

    public PrisonerRecruitmentGlue(IPrisonerRecruitmentHost<TId> host) => h = host ?? throw new ArgumentNullException(nameof(host));

    // ---------- the C++ status texts ----------
    public const string SelectLivingText = "Select a living prisoner.";
    public const string NotRosterText = "This fighter is not an available arena prisoner.";
    public const string BusyText = "Finish the current arena operation before recruiting.";
    public const string UnconsciousText = "The prisoner must be conscious to accept freedom.";
    public const string PlayerNotReadyText = "The player faction is not ready.";
    public const string NotEnoughMarksText = "This prisoner has not earned enough Arena Marks.";
    public const string ReserveFailedText = "The recruitment payment could not be reserved.";
    public const string RollbackFailedText = "Recruitment failed and its payment could not be restored. Check Arena Marks before saving.";
    public const string RecruitFailedText = "Kenshi could not add this prisoner to the current squad; the payment was cancelled.";

    /// <summary>Port only (divergences.md "PrisonerRecruitment"): the recruit's engine members are missing in this game version, so the Recruitment
    /// part is off; no payment is reserved. The C++ has no text for it (Kenshi's recruit always exists).</summary>
    public const string UnavailableText = "This game version cannot add a prisoner to your squad (the recruitment part is off; see the log).";

    /// <summary>Port only: a recruitment that threw outside the transaction's own recovery; the C++ text of a payment whose state is not known,
    /// as the safe advice (as RewardDeliveryLogic.ExceptionText).</summary>
    public const string ExceptionText = RollbackFailedText;

    /// <summary>C++: PrisonerRecruitment.cpp:34-45 (<c>RecruitmentPayment</c>): the binding (the same character, the same identity token) and the
    /// ledger reservation (LeaderboardStore::ReserveProgression).</summary>
    sealed class RecruitmentPayment : RewardTransaction.SelfBoundReservedAccounting
    {
        readonly IPrisonerRecruitmentHost<TId> h;
        readonly TId prisoner;

        // C++: PrisonerRecruitment.cpp:38
        public RecruitmentPayment(IPrisonerRecruitmentHost<TId> host, TId c) { h = host; prisoner = c; }

        // C++: PrisonerRecruitment.cpp:39-44
        public override bool Matches(string identity) => h.IdentityMatches(prisoner, identity);

        /// <summary>Port only: the reservation was accepted (a recovery after an exception must not roll an accepted payment back).</summary>
        public bool Accepted { get; private set; }
        public override bool Accept() { BeforeAcceptForTest?.Invoke(); bool ok = base.Accept(); Accepted |= ok; return ok; }

        /// <summary>Test seam only: runs before each Accept (a test throws from it once).</summary>
        internal Action? BeforeAcceptForTest;
    }

    /// <summary>Test seam only: handed to each payment's <c>BeforeAcceptForTest</c>.</summary>
    internal Action? BeforeAcceptForTest { get; set; }

    /// <summary>C++: PrisonerRecruitment.cpp:46-51 (<c>RecruitAction</c>).</summary>
    sealed class RecruitAction : RewardTransaction.IAction
    {
        readonly IPrisonerRecruitmentHost<TId> h;
        readonly TId prisoner;

        // C++: PrisonerRecruitment.cpp:49
        public RecruitAction(IPrisonerRecruitmentHost<TId> host, TId c) { h = host; prisoner = c; }

        /// <summary>Port only: Apply was called / the recruit happened (the recovery after an exception reads them).</summary>
        public bool Started { get; private set; }
        public bool Applied { get; private set; }

        // C++: PrisonerRecruitment.cpp:50
        public bool Apply()
        {
            Started = true;
            Applied = h.Recruit(prisoner);
            return Applied;
        }
    }

    // C++: PrisonerRecruitment.cpp:54-59
    public float AverageCombatStat(TId prisoner)
    {
        if (h.IsNull(prisoner) || !h.Valid(prisoner) || !h.TryOverallSkill(prisoner, out float skill))
            return 0.0f;
        return skill;
    }

    // C++: PrisonerRecruitment.cpp:61-65
    public int RequiredMarks(TId prisoner)
    {
        return PrisonerRecruitmentLogic.RequiredMarks(
            AverageCombatStat(prisoner));
    }

    /// <summary>C++: PrisonerRecruitment.cpp:67-136 <c>Recruit</c>. Port: an exception never escapes (<see cref="ExceptionText"/>, one Meitou error
    /// line); one inside the transaction is recovered like the failure of the phase it came from (<see cref="Recover"/>).</summary>
    public bool Recruit(TId prisoner, out string status)
    {
        try { return RecruitUnguarded(prisoner, out status); }
        catch (Exception e)
        {
            h.Error("Proving Grounds: prisoner recruitment threw " + e.GetType().Name + ": " + e.Message);
            status = ExceptionText;
            return false;
        }
    }

    bool RecruitUnguarded(TId prisoner, out string status)
    {
        status = "";
        bool recovered = false;
        if (h.IsNull(prisoner) || !h.Valid(prisoner) || h.IsDead(prisoner))
        {
            status = SelectLivingText;
            return false;
        }
        if (!h.IsRosterPrisoner(prisoner))
        {
            status = NotRosterText;
            return false;
        }
        if (h.IngressPending() || h.SparActive() ||
            h.PrisonerRuntimeActivity())
        {
            status = BusyText;
            return false;
        }
        if (h.IsUnconscious(prisoner))
        {
            status = UnconsciousText;
            return false;
        }
        if (!h.PlayerReady())
        {
            status = PlayerNotReadyText;
            return false;
        }

        int cost = RequiredMarks(prisoner);
        if (h.GetMarks(prisoner) < cost)
        {
            status = NotEnoughMarksText;
            return false;
        }
        // port only: the Recruitment part is off (no recruit to call); refused before anything is reserved
        if (!h.RecruitAvailable())
        {
            status = UnavailableText;
            return false;
        }
        string nameBefore = SafeName(prisoner, "");   // port only: the name if a later read of it throws
        var payment = new RecruitmentPayment(h, prisoner) { BeforeAcceptForTest = BeforeAcceptForTest };
        if (!h.ReserveProgression(prisoner, new ArenaLedger.Purchase(cost), payment))
        {
            status = ReserveFailedText;
            return false;
        }

        var recruitment = new RecruitAction(h, prisoner);
        RewardTransaction.Result result;
        try { result = RewardTransaction.CompleteAction(recruitment, payment); }
        catch (Exception e) { result = Recover(prisoner, recruitment, payment, e, out recovered); }
        if (result != RewardTransaction.Result.Success)
        {
            status = result == RewardTransaction.Result.AccountingRollbackFailed
                ? RollbackFailedText
                : RecruitFailedText;
            if (result == RewardTransaction.Result.AccountingRollbackFailed)
            {
                string line = "Proving Grounds: " + status + " (prisoner " + SafeName(prisoner, nameBefore) + ", " + Num(cost) + " Arena Marks)";
                Safe(() => h.Error(line));
            }
            // port only: after an EXCEPTION the recruit happened (Recover): it leaves the roster. Without one (Accept false in the C++ flow) the C++
            // returns here with the recruit still listed (divergences.md "Kept bugs")
            if (recovered) Safe(() => h.ForgetRosterPrisoner(prisoner));
            return false;
        }

        // C++ :120-125 prisoner->setPrisonMode(false, NULL) and, while chained, setChainedMode(false, hand(prisoner)): the prison mode was left
        // inside the recruit itself (IPrisonerRecruitmentHost.Recruit releases the prisoner from its cage first, CAP 15); the chained mode has no
        // Meitou flag to clear (as task 6.2's release, divergences.md "PrisonerUtil (task 6.2")
        // port only (review fix): each step after the paid recruit is guarded on its own, so a throw never turns the success into a failure
        Safe(() => h.ForgetRosterPrisoner(prisoner));

        string costText = Num(cost);
        string name = SafeName(prisoner, nameBefore);
        status = name +
            " earned freedom and joined your squad for " + costText +
            " Arena Marks.";
        Safe(() => h.Debug("Proving Grounds: arena prisoner recruited: " +
            name));
        return true;
    }

    /// <summary>Port only: one step after the paid recruit; a throw is logged (when the error line itself does not throw) and the step skipped.</summary>
    void Safe(Action step)
    {
        try { step(); }
        catch (Exception e)
        {
            try { h.Error("Proving Grounds: prisoner recruitment step threw " + e.GetType().Name + ": " + e.Message); }
            catch (Exception) { }
        }
    }

    string SafeName(TId c, string fallback)
    {
        try { return h.Name(c); }
        catch (Exception) { return fallback; }
    }

    /// <summary>Port only: an exception inside <see cref="RewardTransaction.CompleteAction"/> answered as the phase it came from (the C++ has no
    /// exceptions here). A recruit that happened (Apply returned true, or it threw and the character is now a player character) is never undone
    /// ("Recruitment cannot be reversed safely after native success", RewardTransaction.h:70-71): its payment is accepted now (true: Success),
    /// else AccountingRollbackFailed (the reservation was no longer pending), and <paramref name="recruited"/> tells the caller to
    /// forget the roster prisoner. A recruit that did not happen: the reservation cancelled (best effort), the phase's failure.</summary>
    RewardTransaction.Result Recover(TId prisoner, RecruitAction action, RecruitmentPayment payment, Exception e, out bool recruited)
    {
        recruited = false;
        Safe(() => h.Error("Proving Grounds: prisoner recruitment threw " + e.GetType().Name + ": " + e.Message));
        if (payment.Accepted) return RewardTransaction.Result.Success;   // defensive: Accept is CompleteAction's last step, nothing after it can throw
        bool applied = action.Applied;
        if (!applied && action.Started)
        {
            try { applied = h.JoinedPlayer(prisoner); }
            catch (Exception) { applied = true; }   // not known: never cancel the payment of a recruit that may have happened
        }
        if (applied)
        {
            recruited = true;
            bool accepted;
            try { accepted = payment.Accept(); }
            catch (Exception) { accepted = false; }
            // Accept false = the reservation is no longer pending (the ledger's world ended), so nothing stays held; a payment is never
            // cancelled for a recruit that happened (only an Accept that keeps throwing, which the real ledger cannot, would leave it held)
            return accepted ? RewardTransaction.Result.Success : RewardTransaction.Result.AccountingRollbackFailed;
        }
        var failure = action.Started ? RewardTransaction.Result.DeliveryFailed : RewardTransaction.Result.CompletionFailed;
        bool refunded;
        try { refunded = payment.Rollback(); }
        catch (Exception) { refunded = false; }
        return refunded ? failure : RewardTransaction.Result.AccountingRollbackFailed;
    }

    static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
