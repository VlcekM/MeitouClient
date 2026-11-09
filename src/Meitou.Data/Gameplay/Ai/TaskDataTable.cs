using static Meitou.Data.Gameplay.Ai.TaskDataFlags;

namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// A state of a TaskData list and the value wanted (false for the doc's <c>!</c> entries).
/// </summary>
public readonly record struct TaskCondition(StateType State, bool Holds);

/// <summary>
/// The planner fields of a TaskData row beyond its lists (docs/game/ai-tasks.md "Planner", Observed; <see cref="Acts"/> and
/// <see cref="NeedsTarget"/> Verified).
/// </summary>
[Flags]
public enum TaskDataFlags : byte
{
    /// <summary>
    /// A task of its own the plan can end in (+0x134): set by the registration, cleared by an effect.
    /// </summary>
    Acts = 1,
    /// <summary>
    /// Taken as a provider only when a target is known (+0x48).
    /// </summary>
    NeedsTarget = 2,
    /// <summary>
    /// Has a target finder (+0x160): never a plan's last step, it hands its effect a target.
    /// </summary>
    Finder = 4,
    /// <summary>
    /// Not listed as a provider of the states its list A holds (+0x40).
    /// </summary>
    NotProvider = 8,
    /// <summary>
    /// A provider only for a flagged search or a character without a squad (+0x41).
    /// </summary>
    Restricted = 16,
    /// <summary>
    /// A failed plan for it keeps the queue (+0x23).
    /// </summary>
    KeepsQueue = 32,
}

/// <summary>
/// One TaskData row (docs/game/ai-tasks.md "TaskData registry", Verified, and "Planner"): <see cref="Done"/> is list A (what the row achieves),
/// <see cref="Needs"/> list B (what it needs, at most 11), <see cref="Effect"/> the state a goal row wants another row to achieve;
/// <see cref="Category"/> the registration flag bits (the category registries it is listed in).
/// </summary>
public sealed record TaskDataRow(
    TaskType Type, string Text, int Category, TaskDataFlags Flags, TaskCondition[] Done, TaskCondition[] Needs, TaskCondition? Effect)
{
    /// <summary>
    /// Whether the flag bits hold <paramref name="flag"/>.
    /// </summary>
    public bool Is(TaskDataFlags flag) => (Flags & flag) != 0;
}

/// <summary>
/// The original's 253 TaskData rows in registration order (docs/game/ai-tasks.md), and the providers of each state per category.
/// </summary>
public static class TaskDataTable
{
    /// <summary>
    /// Every row in the original's registration order, transcribed from docs/game/ai-tasks.md "TaskData registry" (state lists as StateType ids,
    /// <c>!</c> wanting false).
    /// </summary>
    public static IReadOnlyList<TaskDataRow> Rows { get; } =
    [
        R(1, "Moving", 0x63, Acts | NeedsTarget, "1 !69 40 41", "27 !24 !83 60 90 59"),
        R(29, "Move order", 0x63, Acts | NeedsTarget | NotProvider, "1", "!24 !83 60 90 59"),
        R(67, "Running", 0x63, Acts | NeedsTarget, "46 40 41", "27 !24 !83 60 90 59"),
        R(82, "Moving to door", 0x61, Acts | NeedsTarget, "57", "27 !24 !83 90 60"),
        R(163, "Moving to door inside", 0x41, Acts | NeedsTarget, "111", "27 !24 !83 90 60"),
        R(83, "Moving to current door", 0x41, Acts, "58", "27 !24 !83"),
        R(199, "Moving to door outside", 0x41, Acts, "126", "27 !24 !83 90 60"),
        R(104, "null", 0x77, 0, "", "20"),
        R(180, "Gate guard", 0x1, Acts, "", "91 52"),
        R(121, "Moving to gate", 0x61, Acts | NeedsTarget, "91", "27 !26 60"),
        R(122, "Open gate", 0x61, Finder | Restricted, "90", "!54", "52"),
        R(123, "Bashing gate", 0x21, Acts | NeedsTarget, "!54 56", "!70 !158 165 5 91 !52"),
        R(290, "Bashing gate", 0x21, Acts | NeedsTarget, "!54 56", "!70 !158 165 5 91 !52"),
        R(272, "Smashing", 0x21, Acts | NeedsTarget, "163", "!70 165 !158 5 46"),
        R(270, "Assaulting town", 0x21, Finder | Restricted, "90", "", "163"),
        R(189, "Worker", 0x1, Finder, "", "", "123"),
        R(190, "Worker", 0x1, Finder, "", "", "136"),
        R(87, "Operating machine", 0x1, Acts | NeedsTarget | KeepsQueue, "123", "38 73 2 !157 !63 !62 !89 !169 93 1"),
        R(221, "Operating machine", 0x1, Acts | NeedsTarget | KeepsQueue, "136", "38 73 2 93 !157 1"),
        R(152, "Operating automatic machine", 0x1, NeedsTarget | Finder, "", "2 !157", "!62"),
        R(124, "Hauling to", 0x1, Acts | NeedsTarget, "", "2 67 1"),
        R(114, "Ditching to make space", 0x1, NeedsTarget | Finder, "82", "", "!62"),
        R(242, "Ditching resources", 0x1, NeedsTarget | Finder, "147", "", "!62"),
        R(184, "Ditching loot", 0x1, Finder, "!121", "", "!62"),
        R(92, "Collecting resource", 0x1, Acts | NeedsTarget, "66 !63 !169", "82 1"),
        R(247, "Collecting resource", 0x1, Acts | NeedsTarget, "150", "82 1"),
        R(284, "Looting", 0x1, Acts | NeedsTarget, "68", "82 1"),
        R(115, "Finding building mats", 0x1, NeedsTarget | Finder, "50", "", "150"),
        R(93, "Filling machine", 0x1, Acts | NeedsTarget, "!62 63", "2 67 !169 1"),
        R(94, "Try filling machine", 0x1, NeedsTarget | Finder, "67", "", "66"),
        R(113, "Try emptying machine", 0x1, NeedsTarget | Finder, "!63 105 !66 !89", "", "!62"),
        R(91, "Unjamming machine", 0x1, NeedsTarget, "!64", "!62 !63"),
        R(90, "Unjam all machines", 0x1, Finder, "!65", "", "!64"),
        R(89, "Keeping everything running", 0x1, 0, "", "!65"),
        R(88, "Delivering", 0x1, Acts | NeedsTarget, "", "1"),
        R(84, "Open current door", 0x41, Finder, "60", "", "53"),
        R(74, "Opening door here", 0x41, Acts, "53", "!55 58"),
        R(75, "Closing door here", 0x41, Acts, "!53", "58"),
        R(85, "Open destination door", 0x41, Finder, "59", "", "52"),
        R(72, "Opening door", 0x41, Acts | NeedsTarget, "52", "!54 57"),
        R(73, "Closing door", 0x41, Acts | NeedsTarget, "!52", "!56 57"),
        R(77, "Locking door", 0x41, Acts | NeedsTarget, "54", "!52 70 !56 57"),
        R(78, "Unlocking door", 0x45, Acts | NeedsTarget, "!54", "70 57"),
        R(140, "Unlocking door", 0x1, Acts | NeedsTarget | Restricted, "!54", "70 57"),
        R(79, "Locking door here", 0x41, Acts | NeedsTarget, "55", "!52 70 58"),
        R(162, "Locking door from inside", 0x41, Acts | NeedsTarget, "112", "70 111 !52"),
        R(198, "Locking door from outside", 0x41, Acts | NeedsTarget, "127", "70 126 !52"),
        R(80, "Unlocking door here", 0x41, Acts | NeedsTarget, "!55", "70 58"),
        R(287, "Bashing door", 0x21, Acts | NeedsTarget, "!55 56 52", "!70 !128 5 57"),
        R(195, "Locking up cages", 0x1, Finder, "", "", "54"),
        R(155, "Locking up", 0x1, Finder, "87", "!108 107", "112"),
        R(197, "Locking up from outside", 0x1, Finder, "", "!108 !107", "127"),
        R(86, "Opening up", 0x1, Finder, "!87", "", "52"),
        R(156, "Entering building", 0x61, Acts, "107", "90 59"),
        R(216, "Ransacking town", 0x1, Finder, "", "5", "107"),
        R(200, "Leaving building", 0x61, Acts | NeedsTarget, "!107", "59"),
        R(76, "Lockpicking", 0x55, Acts | NeedsTarget | Restricted, "!54", "38 166 1 !157"),
        R(201, "Lockpicking Shackles", 0x55, Acts | NeedsTarget, "!114", "38 167 1 !157"),
        R(219, "Removing Shackles", 0x45, Acts, "!134", "!114"),
        R(202, "Escaping", 0x55, NeedsTarget | Restricted, "0", "!134 !79"),
        R(185, "Cutting shackles", 0x55, Acts | NeedsTarget | Restricted, "!114", "38 122 1 !157"),
        R(285, "Cutting lock", 0x55, Acts | NeedsTarget | Restricted, "!54", "38 122 1 !157"),
        R(186, "Breaking shackles", 0x55, Acts | NeedsTarget | NotProvider, "!114", "38 1"),
        R(286, "Breaking lock", 0x55, Acts | NeedsTarget | NotProvider, "!54", "38 1"),
        R(133, "Get out of cage", 0x74, Acts, "!79", "!54"),
        R(256, "Get out of cage", 0x74, Acts, "!79", ""),
        R(207, "Escape cage", 0x74, Acts, "!79", "!54"),
        R(81, "Bashing door", 0x21, Acts | NeedsTarget, "!54 56 52", "!70 !128 5 57"),
        R(226, "Bashing door", 0x21, Acts | NeedsTarget | NotProvider, "!54 56 52", "!70 !128 5 57"),
        R(95, "Repairing", 0x1, Acts | NeedsTarget, "!56 !71", "38 1 !26"),
        R(125, "Engineer", 0x1, Finder, "", "", "2"),
        R(213, "Lifting/Snatching", 0x1, Acts | NeedsTarget | NotProvider, "48", "80 27 !26 7 46"),
        R(240, "Capture Target", 0x1, Acts | NeedsTarget | NotProvider, "48", "95 113 80 27 !26 7 46"),
        R(225, "Lifting/Snatching Order", 0x1, Acts | NeedsTarget | NotProvider, "48", "80 27 !26 46"),
        R(246, "Kidnapping", 0x1, Acts | NeedsTarget | NotProvider, "48", "80 27 !26 7 46"),
        R(68, "Lifting", 0x1, Acts | NeedsTarget, "48", "80 27 !26 46 7"),
        R(170, "Lifting and healing", 0x1, Acts | NeedsTarget, "115", "!78 !22 80 27 !26 46"),
        R(69, "Putting down", 0x1, Acts, "!48 !47", "47"),
        R(2, "Building", 0x1, Acts | NeedsTarget, "2 !71", "38 51 1 !26"),
        R(96, "Demolishing", 0x1, Acts | NeedsTarget, "0", "1"),
        R(71, "Adding materials", 0x1, Acts | NeedsTarget, "51", "50 1 !26"),
        R(3, "Collecting item", 0x65, Acts | NeedsTarget, "3", "15 1"),
        R(14, "Idleness", 0x75, 0, "", "!162 !157"),
        R(28, "Standing", 0x25, Acts, "!24 !25 !26", "!83"),
        R(65, "Getting up", 0x25, Acts, "!162", "45"),
        R(52, "Waking up", 0x65, Acts, "", "45"),
        R(147, "Stumbling", 0x21, Acts, "", "45"),
        R(6, "Equipping weapon", 0x65, Acts, "5", "!139 38"),
        R(264, "Equipping crossbow", 0x65, Acts, "156", "!139 38"),
        R(7, "Sheathing weapon", 0x65, Acts, "!5 !157 !156", ""),
        R(269, "Fixing Legs", 0x45, Acts, "!103", "38 23 !139 !39 !157"),
        R(25, "First aid", 0x45, Acts | NeedsTarget, "!22", "38 23 !140 42 46 !157"),
        R(249, "Splinting", 0x45, Acts | NeedsTarget, "!152", "38 151 !140 42 46 !157"),
        R(60, "Repairing", 0x45, Acts | NeedsTarget, "!133", "38 132 !140 42 46 !157"),
        R(58, "Medic", 0x45, Finder, "", "", "!22"),
        R(250, "Splint Rigging", 0x5, Finder, "", "", "!152"),
        R(57, "Robotics", 0x45, Finder, "", "!39", "!133"),
        R(62, "Standing still", 0x75, Acts, "", "!139 !39"),
        R(64, "Stand still dammit!", 0x45, Acts, "42", "!140 !16"),
        R(237, "Worshiping", 0x41, Finder, "", "", "146"),
        R(236, "Worshiping", 0x41, Acts | NeedsTarget, "146", "!5 !47"),
        R(131, "Looting food", 0x1, Finder, "", "", "68"),
        R(259, "Picking up food", 0x71, Finder, "149", "", "3"),
        R(277, "Fetch", 0x20, Finder, "149", "", "3"),
        R(278, "Playing 'Limbs'", 0x20, Acts, "!149", ""),
        R(279, "Limb Jealousy", 0x20, Finder, "", "", "28"),
        R(243, "Getting food", 0x61, Finder, "", "", "149"),
        R(244, "Grabbing food", 0x61, Acts | NeedsTarget, "149", "!12 82 1"),
        R(8, "Finding weapon", 0x45, Finder, "4", "", "3"),
        R(177, "Looting the dead", 0x45, Finder, "", "", "3"),
        R(212, "Confiscating goods", 0x45, Finder, "", "", "3"),
        R(26, "Looting", 0x45, Acts | NeedsTarget, "!0", "1"),
        R(238, "Foraging animals", 0x1, Finder, "", "82", "148"),
        R(241, "Looting resources", 0x5, Acts | NeedsTarget, "148", "1"),
        R(35, "Running away", 0x61, Acts, "!39 6", "60 !26"),
        R(280, "Running away", 0x61, Acts, "", "60 !26"),
        R(128, "Retreating", 0x63, Acts, "", "!26"),
        R(127, "Retreating fast", 0x63, Acts, "95", "!26"),
        R(19, "Going home", 0x61, Finder, "18", "", "1"),
        R(59, "Get ready", 0x65, 0, "37", "!26 5"),
        R(4, "Attacking", 0x41, Acts | NeedsTarget, "6 7", "!140 !139 27 60 90 59 37 !69 41"),
        R(262, "Shooting", 0x1, Acts | NeedsTarget, "6 7", "!139 !158 161 !26 156"),
        R(263, "Shooting", 0x1, Acts | NeedsTarget | NotProvider, "7", "!139 !158 161 !26 156"),
        R(266, "Shooting", 0x1, Acts | NeedsTarget | NotProvider, "7", "!139 !158 161 !26 156"),
        R(228, "Taking out", 0x1, Acts | NeedsTarget | NotProvider, "", "!140 27 60 90 59 38 !69"),
        R(227, "Attacking", 0x20, Acts | NeedsTarget, "6 7", "27 !78 60 90 59 37 !69 5 41"),
        R(13, "Attacking attackers of", 0x61, NeedsTarget | Finder, "!16 !39 !12", "", "7"),
        R(34, "Attacking trouble makers", 0x21, Finder, "!29", "", "7"),
        R(149, "Turret guard", 0x1, Finder, "6", "38", "104"),
        R(222, "Turret home guard", 0x1, Finder, "6", "", "104"),
        R(146, "Turret gunner", 0x1, Acts | NeedsTarget, "104 145", "38 !47 !158 2 93 !157 1"),
        R(235, "Turret gunner", 0x1, Acts | NeedsTarget | NotProvider, "7", "145"),
        R(234, "Turret guard", 0x1, Finder, "6 !39 !12", "38", "104"),
        R(101, "Assault", 0x61, NeedsTarget | Finder, "", "!47", "7"),
        R(248, "Attacking Squad", 0x61, NeedsTarget | Finder, "", "!159 !47", "7"),
        R(165, "Stalking", 0x1, Acts | NeedsTarget, "", ""),
        R(103, "Diplomatting", 0x1, Acts | NeedsTarget, "76", "!101 131"),
        R(129, "Approaching fast", 0x1, Acts | NeedsTarget, "76", "!101 131"),
        R(159, "Talking", 0x41, Acts | NeedsTarget, "109", "1"),
        R(161, "Talking", 0x55, Acts | NeedsTarget, "110", ""),
        R(12, "Talking", 0x45, Acts | NeedsTarget, "0", "1"),
        R(102, "Making announcement", 0x41, Acts | NeedsTarget, "0", "1"),
        R(164, "Following and talking", 0x41, Acts | NeedsTarget, "0", "!26 !158 28"),
        R(188, "Working the slaves", 0x1, Finder, "0", "", "109"),
        R(194, "Selling slaves", 0x1, Finder, "!155", "", "109"),
        R(218, "Selling slaves", 0x1, Finder, "!155", "", "109"),
        R(192, "Delivering slaves", 0x3, 0, "", "!26 36"),
        R(158, "Shooing", 0x41, Finder, "!108", "107 !130", "110"),
        R(283, "Shooing", 0x41, Finder, "!108", "107 !130", "110"),
        R(17, "Protection", 0x61, NeedsTarget, "", "!16 !22"),
        R(16, "Attacking enemies", 0x61, Finder, "", "", "6"),
        R(41, "Fighting for master", 0x21, Finder, "", "", "6"),
        R(21, "Attacking others", 0x61, Finder, "", "", "7"),
        R(144, "Home territory aggression", 0x61, Finder, "", "", "6"),
        R(32, "Self preservation", 0x65, Finder, "!159", "", "!39"),
        R(45, "Bodyguard", 0x61, NeedsTarget, "", "!16 !39"),
        R(44, "Following", 0x61, Acts | NeedsTarget | NotProvider, "28", "!16 60 90 59"),
        R(253, "Following", 0x61, Acts | NeedsTarget | NotProvider, "28", "!7 60 90 59"),
        R(40, "Following", 0x61, Acts | NeedsTarget, "28", "!7 !16 60 90 59"),
        R(141, "Following leader", 0x61, Acts, "", "60 90 59 !26"),
        R(171, "Following master", 0x61, Acts, "", ""),
        R(31, "Staying close", 0x61, Acts | NeedsTarget, "", "!7 !16 60 90 59"),
        R(289, "Staying close", 0x20, Acts | NeedsTarget, "", "!7 !16 60 90 168"),
        R(166, "Restraining", 0x41, Acts | NeedsTarget, "134 !124", "38 46 118 7"),
        R(181, "Stripping weapons", 0x41, Acts | NeedsTarget, "118", "38 46 7"),
        R(182, "Processing slave", 0x1, Acts | NeedsTarget, "120", "46 134 42"),
        R(183, "Watching slaves", 0x1, Finder, "", "", "120"),
        R(260, "Watching for new slaves", 0x1, Finder, "", "", "120"),
        R(167, "Capturing slave", 0x1, Finder, "", "", "134"),
        R(174, "Ordering slaves", 0x1, Acts | NeedsTarget, "116", "38 125 134 1"),
        R(173, "Gathering slaves", 0x1, Finder, "", "", "116"),
        R(217, "Gathering slaves for shipping", 0x1, Finder, "", "", "116"),
        R(245, "Gathering unholy slaves", 0x1, Finder, "", "", "116"),
        R(187, "Obedient Slave", 0x75, 0, "0", ""),
        R(168, "Carrying slaves", 0x1, Finder, "", "", "115"),
        R(169, "Putting down", 0x1, 0, "", "!47"),
        R(15, "Protect allies", 0x61, Finder, "", "", "!12"),
        R(288, "Protect allies", 0x61, Finder, "", "", "!12"),
        R(143, "Protect close allies", 0x61, Finder, "", "", "!16"),
        R(30, "Holding position", 0x75, NeedsTarget, "", "", "1"),
        R(51, "Stand at node", 0x61, Acts | NeedsTarget, "31", "1"),
        R(20, "Shopkeeper", 0x41, Finder, "", "", "31"),
        R(38, "Static guard", 0x61, Finder, "", "!47", "31"),
        R(268, "Static guard indoors", 0x61, Finder, "", "!47", "31"),
        R(157, "Static town guard", 0x61, Finder, "", "!47", "31"),
        R(47, "general node", 0x1, Finder, "", "", "31"),
        R(42, "Getting a weapon", 0x45, 0, "", "38 4"),
        R(176, "Blood tracking", 0x61, Acts, "", "!26 60"),
        R(22, "Patrolling", 0x21, Acts, "", "!26 60 !157"),
        R(36, "Patrolling town", 0x21, Acts, "", "94 !47 !26 60 !157"),
        R(37, "Wandering town", 0x1, Finder, "", "!26 !157", "31"),
        R(118, "Shopping", 0x1, Finder, "", "86", "85"),
        R(119, "Shopping", 0x1, Acts | NeedsTarget, "85", "1"),
        R(214, "Relaxing", 0x1, Finder, "", "86", "123"),
        R(43, "Staying home", 0x61, Finder, "", "!26 !157 18", "136"),
        R(255, "Reigning", 0x41, Finder, "", "!26 !157 18", "136"),
        R(275, "Sitting around", 0x1, Finder, "", "!26 !47 !157 86", "136"),
        R(24, "Wandering", 0x21, Finder, "86", "", "1"),
        R(39, "Wandering trader", 0x1, 0, "", "!157 32 36"),
        R(55, "Recruiting", 0x1, NeedsTarget | Finder, "", "", "1"),
        R(53, "Travelling", 0x61, Acts, "36", "!26 32"),
        R(130, "Travelling quickly", 0x61, Acts, "94", "!26 32"),
        R(215, "Mission formation", 0x61, Acts, "131", "!26"),
        R(5, "Attacking target", 0x61, Acts | NeedsTarget | NotProvider, "7", "!140 !78 37 !69"),
        R(61, "Attacking target unprovoked", 0x61, Acts | NeedsTarget | NotProvider, "7", "!140 !78 37 !69"),
        R(150, "Prospecting", 0x41, Acts, "", "!139"),
        R(97, "Training", 0x1, Acts | NeedsTarget | NotProvider, "", "38 73 1"),
        R(153, "Going to bed", 0x41, Finder, "", "!141", "83"),
        R(239, "Going to bed", 0x41, Finder, "", "!108 87", "83"),
        R(98, "Sleeping", 0x41, Acts | NeedsTarget, "72 83", "!130 73 !157 !47 1"),
        R(258, "Sleeping", 0x41, Acts | NeedsTarget | NotProvider, "", "!130 73 !157 !47 1"),
        R(175, "Sleeping on ground", 0x61, Acts, "72", "6 !29 !95 !157 !47"),
        R(261, "Hiding", 0x61, Acts, "72", "6 !29 !157 !47"),
        R(99, "Put in bed", 0x1, Acts | NeedsTarget, "77", "73 47 1"),
        R(116, "Getting out of bed", 0x41, Acts, "!83", "45"),
        R(208, "Getting out of bed!", 0x41, Acts, "!83", "45"),
        R(257, "Getting out of bed", 0x41, Acts, "!83", "45"),
        R(105, "Find and rescue", 0x1, Finder, "", "", "48"),
        R(148, "Find and rescue", 0x1, Finder, "", "", "48"),
        R(106, "Find and put in bed", 0x1, NeedsTarget | Finder, "", "", "77"),
        R(100, "Sleeping", 0x41, Acts | NeedsTarget, "72", ""),
        R(142, "Find and rescue leader", 0x1, Finder, "", "101", "48"),
        R(135, "Kill random prisoner", 0x1, Finder, "", "", "!98"),
        R(134, "Kill prisoner", 0x1, Acts | NeedsTarget, "!98", "5 1"),
        R(139, "Eat random prisoner", 0x21, Finder, "", "", "135"),
        R(282, "Dinner time", 0x21, Finder, "", "", "135"),
        R(230, "Eat corpse", 0x61, Finder, "", "", "135"),
        R(233, "Eat body", 0x61, Finder, "", "", "135"),
        R(232, "Find crops", 0x61, Finder, "", "", "!144"),
        R(231, "Eating crops", 0x61, Acts | NeedsTarget, "!144", "143 1"),
        R(220, "Eat prisoner", 0x61, Acts | NeedsTarget, "135", "142 !47 !157 1"),
        R(196, "Beat prisoner", 0x1, Acts | NeedsTarget, "7", "78 5 1"),
        R(136, "Disposing of corpse", 0x1, Acts | NeedsTarget, "100", "99 1"),
        R(138, "Find corpses", 0x41, Finder, "99", "", "48"),
        R(137, "Corpse disposal", 0x1, NeedsTarget | Finder, "", "99", "100"),
        R(107, "Imprisoned", 0x55, Acts, "79", "73 1"),
        R(108, "Put in prison", 0x1, Acts | NeedsTarget, "81", "73 47 1"),
        R(112, "Find and put in cage", 0x1, NeedsTarget | Finder, "154", "!99", "81"),
        R(281, "Find and put in cage", 0x1, NeedsTarget | Finder, "154", "", "81"),
        R(172, "Getting in my cage", 0x55, Finder, "", "", "79"),
        R(211, "Freeing prisoner", 0x1, Finder, "", "", "!78"),
        R(276, "Liberating prisoners", 0x1, Finder, "", "", "!78"),
        R(110, "Release from prison", 0x1, Acts | NeedsTarget, "0 !78", "125 !73 1"),
        R(203, "Arresting", 0x1, NeedsTarget, "129", "7 118 48"),
        R(204, "Bounty hunting", 0x1, Finder, "", "", "129"),
        R(223, "Remove Home Intruder", 0x1, Finder, "", "!47", "48"),
        R(224, "Intruder out", 0x41, Acts, "137", "47 60"),
        R(273, "Remove Intruder", 0x1, Finder, "", "!47", "48"),
        R(274, "Throwing out", 0x41, Acts, "164", "47 60 90"),
        R(209, "Investigating", 0x1, Acts, "!130", "86"),
        R(210, "Investigating", 0x1, Acts, "!130", "86"),
        R(132, "Find and kidnap", 0x1, Finder, "153", "!47", "48"),
        R(254, "Kidnapper", 0x1, 0, "0", "153 154"),
    ];

    /// <summary>
    /// The row of each task type by its number (0 to 290); null where a type has none.
    /// </summary>
    static readonly TaskDataRow?[] ByType = BuildByType();

    /// <summary>
    /// <see cref="Providers"/> results by (category, state), filled on first ask from any thread.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, TaskCondition), TaskDataRow[]> ProviderCache = new();

    /// <summary>
    /// The row of a type; null for the 37 named types without one.
    /// </summary>
    public static TaskDataRow? Of(TaskType type) => (uint)type < (uint)ByType.Length ? ByType[(int)type] : null;

    /// <summary>
    /// The rows of a category (a registration flag bit) whose list A holds a state with the wanted value, in ascending task type (the original
    /// fills them from its registry map keyed by type), not <see cref="TaskDataFlags.NotProvider"/> ones (docs/game/ai-tasks.md "Planner": the
    /// provider maps).
    /// </summary>
    public static IReadOnlyList<TaskDataRow> Providers(int category, TaskCondition state) =>
        ProviderCache.GetOrAdd((category, state), static k =>
            [.. Rows.Where(r => (r.Category & k.Item1) != 0 && !r.Is(NotProvider) && r.Done.Contains(k.Item2)).OrderBy(r => (int)r.Type)]);

    /// <summary>
    /// Indexes <see cref="Rows"/> by task type.
    /// </summary>
    static TaskDataRow?[] BuildByType()
    {
        var a = new TaskDataRow?[291];
        foreach (var r in Rows) a[(int)r.Type] = r;
        return a;
    }

    /// <summary>
    /// A row from its type number, text, category bits, flags and its lists written as space-separated StateType ids (<c>!</c> for false); the effect
    /// is the first entry of its list, null when none is given.
    /// </summary>
    static TaskDataRow R(int type, string text, int category, TaskDataFlags flags, string done, string needs, string? effect = null) =>
        new((TaskType)type, text, category, flags, List(done), List(needs), effect is null ? null : List(effect)[0]);

    /// <summary>
    /// Parses a space-separated list of StateType ids, a leading <c>!</c> wanting the state false.
    /// </summary>
    static TaskCondition[] List(string s) =>
        [.. s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(e =>
            e[0] == '!' ? new TaskCondition((StateType)int.Parse(e[1..]), false) : new TaskCondition((StateType)int.Parse(e), true))];
}