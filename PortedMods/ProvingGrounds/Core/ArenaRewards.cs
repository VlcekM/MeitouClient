// Port of upstream/ProvingGrounds/plugin/src/ArenaRewards.h and ArenaRewards.cpp (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>Pure Warlord gear reward catalog and progression rules (ArenaRewards.h:1; PA §2.17). Every row, string, quality and
/// cost is the C++ table's (ArenaRewards.cpp:14-277); record ids come from <see cref="PgRecords"/>, picked by id, never by name
/// (146 is shown as "Pit Cleaver", 139 as "Warlord Hacker", 78 as "Warlord's Judgement").
/// <para><b>The mutable catalogue.</b> The C++ tables <c>kLicences</c>, <c>kGeneralRewards</c>, <c>kWeaponLicence</c> and
/// <c>kRewards</c> are non-const statics (ArenaRewards.cpp:103-125) that <see cref="ApplyPriceOverrides"/> stamps IN PLACE, each
/// price passed to PGConfig as the default of its own replacement. Here the four tables live in one <see cref="Catalogue"/>
/// object: production reads the one process-wide instance (built from the shipped tables), exactly like the C++ globals; a test
/// swaps in a fresh <see cref="Catalogue.Shipped"/> for its own flow with <see cref="UseForTest"/> (the AsyncLocal pattern of
/// PGConfig and BalanceTuning), so parallel tests never see each other's stamping. Like the C++, nothing guards against a second
/// call: ApplyPriceOverrides stamps whatever is there now, so calling it twice at a multiplier other than 1 scales twice. The
/// original calls it once, from PGConfig::Load (ArenaRewards.h:141-145).</para>
/// <para>The readers return copies of the structs (the C++ returns <c>const&amp;</c> into the tables); a copy taken before
/// ApplyPriceOverrides keeps the old price, where a C++ reference would see the new one. The glue reads after load.</para></summary>
public static class ArenaRewards
{
    /// <summary>C++: ArenaRewards.h:6-34</summary>
    public enum Piece
    {
        Greatsword = 0,
        Cloak,
        PlateSkirt,
        PlateBoots,
        PlateArmour,
        Helmet,
        Katana,
        Polearm,
        PitfighterFootwraps,
        PitfighterHarness,
        PitfighterJawGuard,
        PitfighterPants,
        RetainerPants,
        RetainerJacket,
        RetainerBoots,
        RetainerHelmet,
        EnforcerLegplates,
        EnforcerBoots,
        EnforcerChest,
        EnforcerHelmet,
        WarlordLongsword,
        WarlordKatana,
        Mace,
        Sabre,
        Hacker,
        Count
    }

    /// <summary>C++: ArenaRewards.h:36-42</summary>
    public enum ArmourSet
    {
        T1 = 0,
        T2,
        T3,
        None
    }

    /// <summary>C++: ArenaRewards.h:44-51</summary>
    public enum ArmourRow
    {
        Pitfighter = 0,
        Retainer,
        Enforcer,
        Warlord,
        Count
    }

    /// <summary>C++: ArenaRewards.h:53-62. Armour uses all four tiers. Weapons start at High Grade and reuse the
    /// final three enum slots with their Crucible forge-grade names.</summary>
    public enum Tier
    {
        Standard = 0,
        HighGrade,
        Specialist,
        Masterwork,
        MaxCount
    }

    /// <summary>C++: ArenaRewards.h:64-77 (a value type; the C++ hands out <c>const Reward&amp;</c>).</summary>
    public struct Reward
    {
        public Piece Piece;
        public Tier Tier;
        public string PieceName;
        public string TierName;
        public string StringId;
        /// <summary>Weapon mesh/model for this grade; null for armour.</summary>
        public string? ModelStringId;
        /// <summary>Weapon manufacturer for this grade; null for armour.</summary>
        public string? ManufacturerStringId;
        public int QualityLevel;
        public int Cost;
    }

    /// <summary>C++: ArenaRewards.h:79-86</summary>
    public struct Licence
    {
        public ArmourSet Set;
        public string StableId;
        public string Name;
        public int Cost;
        public string? PrerequisiteId;
    }

    /// <summary>C++: ArenaRewards.h:88-99</summary>
    public enum GeneralItem
    {
        ArenaBuildingsI = 0,
        ArenaBuildingsII,
        AdvancedFirstAid,
        SplintKit,
        SkeletonRepair,
        PitSurgeonRoll,
        BoneSetterBraces,
        IronmenderCase,
        Count
    }

    /// <summary>C++: ArenaRewards.h:101-115</summary>
    public struct GeneralReward
    {
        public GeneralItem Item;
        public string StableId;
        public string Name;
        public string StringId;
        /// <summary>Research record wrapped by Kenshi's runtime blueprint item. Null
        /// for rewards that are delivered as their catalog item directly.</summary>
        public string? BlueprintResearchStringId;
        public int Cost;
        public int Charges;
        public bool Repeatable;
        public string? PrerequisiteId;
        public string? GrantsUnlockId;
    }

    /// <summary>C++: ArenaRewards.h:153-158</summary>
    public enum OfferKind
    {
        Claim = 0,
        Replace,
        Complete
    }

    /// <summary>C++: ArenaRewards.h:160-167. Pure offer resolution given whether the fighter still holds the
    /// previous / any grade of the piece in inventory or equipped.</summary>
    public struct Offer
    {
        public OfferKind Kind;
        public Tier Tier;
        public int Cost;
    }

    // Kenshi serializes a weapon's model through the manufacturer table
    // using its level_0_100 value. These values must therefore match the
    // FCS "weapon models" records exactly as well as setting its balance:
    // Arena Forge models are 55, Pit Forge models are 75, and Warlord
    // Forge models are 95.
    // Armour Standard/High/Specialist/Masterwork is 40/60/80/95.
    // C++: ArenaRewards.cpp:14-41
    static readonly int[] kTierForPiece =
    {
        3, // Greatsword (Crucible Forge models)
        4, // Cloak
        4, // Plateskirt
        4, // Plate Boots
        4, // Plate Armour
        4, // Helmet
        3, // Nodachi (Crucible Forge models)
        3, // Polearm (Crucible Forge models)
        4, // Pitfighter Footwraps
        4, // Pitfighter Harness
        4, // Pitfighter Jaw Guard
        4, // Pitfighter Pants
        4, // Retainer Leather Pants
        4, // Retainer Leather Jacket
        4, // Retainer Boots
        4, // Retainer Leather Helmet
        4, // Enforcer Legplates
        4, // Enforcer Plated Boots
        4, // Plate Enforcer Chest
        4, // Enforcer Helmet
        3, // Warlord Longsword
        3, // Warlord Katana
        3, // Mace
        3, // Sabre
        3  // Hacker
    };

    // C++: ArenaRewards.cpp:43-70
    static readonly Tier[] kFirstTierForPiece =
    {
        Tier.HighGrade, // Greatsword
        Tier.Standard,  // Cloak
        Tier.Standard,  // Plateskirt
        Tier.Standard,  // Plate Boots
        Tier.Standard,  // Plate Armour
        Tier.Standard,  // Helmet
        Tier.HighGrade, // Nodachi
        Tier.HighGrade, // Polearm
        Tier.Standard,  // Pitfighter Footwraps
        Tier.Standard,  // Pitfighter Harness
        Tier.Standard,  // Pitfighter Jaw Guard
        Tier.Standard,  // Pitfighter Pants
        Tier.Standard,  // Retainer Leather Pants
        Tier.Standard,  // Retainer Leather Jacket
        Tier.Standard,  // Retainer Boots
        Tier.Standard,  // Retainer Leather Helmet
        Tier.Standard,  // Enforcer Legplates
        Tier.Standard,  // Enforcer Plated Boots
        Tier.Standard,  // Plate Enforcer Chest
        Tier.Standard,  // Enforcer Helmet
        Tier.HighGrade, // Warlord Longsword
        Tier.HighGrade, // Warlord Katana
        Tier.HighGrade, // Mace
        Tier.HighGrade, // Sabre
        Tier.HighGrade  // Hacker
    };

    // C++: ArenaRewards.cpp:72-99
    static readonly string[] kStableIds =
    {
        "weapon.greatsword",
        "armour.warlord.cloak",
        "armour.warlord.skirt",
        "armour.warlord.boots",
        "armour.warlord.chest",
        "armour.warlord.helmet",
        "weapon.katana",
        "weapon.polearm",
        "armour.pitfighter.footwraps",
        "armour.pitfighter.harness",
        "armour.pitfighter.jaw_guard",
        "armour.pitfighter.pants",
        "armour.retainer.pants",
        "armour.retainer.jacket",
        "armour.retainer.boots",
        "armour.retainer.helmet",
        "armour.enforcer.legplates",
        "armour.enforcer.boots",
        "armour.enforcer.chest",
        "armour.enforcer.helmet",
        "weapon.warlord_longsword",
        "weapon.warlord_katana",
        "weapon.mace",
        "weapon.sabre",
        "weapon.hacker"
    };

    /// <summary>The four mutable tables of ArenaRewards.cpp:101-277 (the C++ anonymous-namespace globals). Prices are stamped from
    /// PGConfig at load, so these tables are the catalogue's shipped defaults rather than its final word.</summary>
    internal sealed class Catalogue
    {
        // C++: ArenaRewards.cpp:103-108
        public Licence[] Licences = null!;
        // C++: ArenaRewards.cpp:110-120
        public GeneralReward[] GeneralRewards = null!;
        // C++: ArenaRewards.cpp:122-123
        public Licence WeaponLicence;
        // C++: ArenaRewards.cpp:125-277, [PieceCount][TierMaxCount]
        public Reward[,] Rewards = null!;

        /// <summary>A fresh catalogue holding the shipped tables (no price stamped yet).</summary>
        public static Catalogue Shipped()
        {
            var c = new Catalogue();
            c.Licences = new[]
            {
                new Licence { Set = ArmourSet.T1, StableId = "armour.retainer", Name = "RETAINER LEATHER", Cost = 50, PrerequisiteId = null },
                new Licence { Set = ArmourSet.T2, StableId = "armour.enforcer", Name = "PLATE ENFORCER", Cost = 100, PrerequisiteId = "armour.retainer" },
                new Licence { Set = ArmourSet.T3, StableId = "armour.warlord", Name = "WARLORD", Cost = 150, PrerequisiteId = "armour.enforcer" },
            };
            c.GeneralRewards = new[]
            {
                G(GeneralItem.ArenaBuildingsI, "blueprint.arena1", "Arena Buildings I", PgRecords.BlueprintArenaBuildingsI, PgRecords.ResearchProvingGrounds, 25, 0, true, null, "blueprint.arena1"), // C++: ArenaRewards.cpp:112
                G(GeneralItem.ArenaBuildingsII, "blueprint.arena2", "Arena Buildings II", PgRecords.BlueprintArenaBuildingsII, PgRecords.ResearchProvingGroundsII, 75, 0, true, "blueprint.arena1", null), // C++: ArenaRewards.cpp:113
                G(GeneralItem.AdvancedFirstAid, "supply.advanced_first_aid", "Advanced First Aid Kit", PgRecords.AdvancedFirstAidKit, null, 5, 0, true, null, null), // C++: ArenaRewards.cpp:114
                G(GeneralItem.SplintKit, "supply.splint", "Splint Kit", PgRecords.SplintKit, null, 4, 0, true, null, null), // C++: ArenaRewards.cpp:115
                G(GeneralItem.SkeletonRepair, "supply.skeleton_repair", "Skeleton Repair Kit", PgRecords.SkeletonRepairKit, null, 10, 0, true, null, null), // C++: ArenaRewards.cpp:116
                G(GeneralItem.PitSurgeonRoll, "supply.pit_surgeon_roll", "Pit Surgeon Roll", PgRecords.PitSurgeonRoll, null, 7, 1200, true, null, null), // C++: ArenaRewards.cpp:117
                G(GeneralItem.BoneSetterBraces, "supply.bone_setter_braces", "Bone-Setter Braces", PgRecords.BoneSetterBraces, null, 6, 125, true, null, null), // C++: ArenaRewards.cpp:118
                G(GeneralItem.IronmenderCase, "supply.ironmender_case", "Ironmender Case", PgRecords.IronmenderCase, null, 15, 600, true, null, null), // C++: ArenaRewards.cpp:119
            };
            c.WeaponLicence = new Licence { Set = ArmourSet.None, StableId = "weapon.all", Name = "WEAPONS", Cost = 50, PrerequisiteId = null };
            Reward[] rows =
            {
                R(Piece.Greatsword, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:128
                R(Piece.Greatsword, Tier.HighGrade, "Warlord Greatsword", "Pit Reaver", PgRecords.WarlordGreatsword, PgRecords.PitReaver, PgRecords.ArenaForge, 55, 100), // C++: ArenaRewards.cpp:129
                R(Piece.Greatsword, Tier.Specialist, "Warlord Greatsword", "Arena Reaver", PgRecords.WarlordGreatsword, PgRecords.ArenaReaver, PgRecords.ArenaForge, 75, 150), // C++: ArenaRewards.cpp:130
                R(Piece.Greatsword, Tier.Masterwork, "Warlord Greatsword", "Warlord's Judgement", PgRecords.WarlordGreatsword, PgRecords.WarlordsJudgment, PgRecords.NullForged, 95, 200), // C++: ArenaRewards.cpp:131
                R(Piece.Cloak, Tier.Standard, "Warlord Cloak", "Standard", PgRecords.WarlordCloakNeck, null, null, 40, 50), // C++: ArenaRewards.cpp:134
                R(Piece.Cloak, Tier.HighGrade, "Warlord Cloak", "High Grade", PgRecords.WarlordCloakNeck, null, null, 60, 75), // C++: ArenaRewards.cpp:135
                R(Piece.Cloak, Tier.Specialist, "Warlord Cloak", "Specialist", PgRecords.WarlordCloakNeck, null, null, 80, 100), // C++: ArenaRewards.cpp:136
                R(Piece.Cloak, Tier.Masterwork, "Warlord Cloak", "Masterwork", PgRecords.WarlordCloakNeck, null, null, 95, 150), // C++: ArenaRewards.cpp:137
                R(Piece.PlateSkirt, Tier.Standard, "Warlord Plateskirt", "Standard", PgRecords.WarlordPlateskirt, null, null, 40, 50), // C++: ArenaRewards.cpp:140
                R(Piece.PlateSkirt, Tier.HighGrade, "Warlord Plateskirt", "High Grade", PgRecords.WarlordPlateskirt, null, null, 60, 75), // C++: ArenaRewards.cpp:141
                R(Piece.PlateSkirt, Tier.Specialist, "Warlord Plateskirt", "Specialist", PgRecords.WarlordPlateskirt, null, null, 80, 100), // C++: ArenaRewards.cpp:142
                R(Piece.PlateSkirt, Tier.Masterwork, "Warlord Plateskirt", "Masterwork", PgRecords.WarlordPlateskirt, null, null, 95, 150), // C++: ArenaRewards.cpp:143
                R(Piece.PlateBoots, Tier.Standard, "Warlord Plate Boots", "Standard", PgRecords.WarlordPlateBoots, null, null, 40, 50), // C++: ArenaRewards.cpp:146
                R(Piece.PlateBoots, Tier.HighGrade, "Warlord Plate Boots", "High Grade", PgRecords.WarlordPlateBoots, null, null, 60, 75), // C++: ArenaRewards.cpp:147
                R(Piece.PlateBoots, Tier.Specialist, "Warlord Plate Boots", "Specialist", PgRecords.WarlordPlateBoots, null, null, 80, 100), // C++: ArenaRewards.cpp:148
                R(Piece.PlateBoots, Tier.Masterwork, "Warlord Plate Boots", "Masterwork", PgRecords.WarlordPlateBoots, null, null, 95, 150), // C++: ArenaRewards.cpp:149
                R(Piece.PlateArmour, Tier.Standard, "Warlord Plate Armour", "Standard", PgRecords.WarlordPlateArmour, null, null, 40, 60), // C++: ArenaRewards.cpp:152
                R(Piece.PlateArmour, Tier.HighGrade, "Warlord Plate Armour", "High Grade", PgRecords.WarlordPlateArmour, null, null, 60, 80), // C++: ArenaRewards.cpp:153
                R(Piece.PlateArmour, Tier.Specialist, "Warlord Plate Armour", "Specialist", PgRecords.WarlordPlateArmour, null, null, 80, 120), // C++: ArenaRewards.cpp:154
                R(Piece.PlateArmour, Tier.Masterwork, "Warlord Plate Armour", "Masterwork", PgRecords.WarlordPlateArmour, null, null, 95, 180), // C++: ArenaRewards.cpp:155
                R(Piece.Helmet, Tier.Standard, "Warlord Helmet", "Standard", PgRecords.WarlordHelmet, null, null, 40, 80), // C++: ArenaRewards.cpp:158
                R(Piece.Helmet, Tier.HighGrade, "Warlord Helmet", "High Grade", PgRecords.WarlordHelmet, null, null, 60, 100), // C++: ArenaRewards.cpp:159
                R(Piece.Helmet, Tier.Specialist, "Warlord Helmet", "Specialist", PgRecords.WarlordHelmet, null, null, 80, 150), // C++: ArenaRewards.cpp:160
                R(Piece.Helmet, Tier.Masterwork, "Warlord Helmet", "Masterwork", PgRecords.WarlordHelmet, null, null, 95, 200), // C++: ArenaRewards.cpp:161
                R(Piece.Katana, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:164
                R(Piece.Katana, Tier.HighGrade, "Warlord Nodachi", "Pit Fang", PgRecords.WarlordNodachi, PgRecords.PitFang, PgRecords.ArenaForge, 55, 60), // C++: ArenaRewards.cpp:165
                R(Piece.Katana, Tier.Specialist, "Warlord Nodachi", "Arena Fang", PgRecords.WarlordNodachi, PgRecords.ArenaFang, PgRecords.ArenaForge, 75, 120), // C++: ArenaRewards.cpp:166
                R(Piece.Katana, Tier.Masterwork, "Warlord Nodachi", "Warlord's Fang", PgRecords.WarlordNodachi, PgRecords.WarlordsFang, PgRecords.NullForged, 95, 180), // C++: ArenaRewards.cpp:167
                R(Piece.Polearm, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:170
                R(Piece.Polearm, Tier.HighGrade, "Warlord Polearm", "Pit Cleaver", PgRecords.WarlordPolearm, PgRecords.PitCleaver, PgRecords.ArenaForge, 55, 85), // C++: ArenaRewards.cpp:171
                R(Piece.Polearm, Tier.Specialist, "Warlord Polearm", "Arena Cleaver", PgRecords.WarlordPolearm, PgRecords.ArenaCleaver, PgRecords.ArenaForge, 75, 130), // C++: ArenaRewards.cpp:172
                R(Piece.Polearm, Tier.Masterwork, "Warlord Polearm", "Warlord's Cleaver", PgRecords.WarlordPolearm, PgRecords.WarlordsCleaver, PgRecords.NullForged, 95, 170), // C++: ArenaRewards.cpp:173
                R(Piece.PitfighterFootwraps, Tier.Standard, "Pitfighter Footwraps", "Standard", PgRecords.PitfighterFootwraps, null, null, 40, 20), // C++: ArenaRewards.cpp:176
                R(Piece.PitfighterFootwraps, Tier.HighGrade, "Pitfighter Footwraps", "High Grade", PgRecords.PitfighterFootwraps, null, null, 60, 40), // C++: ArenaRewards.cpp:177
                R(Piece.PitfighterFootwraps, Tier.Specialist, "Pitfighter Footwraps", "Specialist", PgRecords.PitfighterFootwraps, null, null, 80, 60), // C++: ArenaRewards.cpp:178
                R(Piece.PitfighterFootwraps, Tier.Masterwork, "Pitfighter Footwraps", "Masterwork", PgRecords.PitfighterFootwraps, null, null, 95, 80), // C++: ArenaRewards.cpp:179
                R(Piece.PitfighterHarness, Tier.Standard, "Pitfighter Harness", "Standard", PgRecords.PitfighterHarness, null, null, 40, 25), // C++: ArenaRewards.cpp:182
                R(Piece.PitfighterHarness, Tier.HighGrade, "Pitfighter Harness", "High Grade", PgRecords.PitfighterHarness, null, null, 60, 50), // C++: ArenaRewards.cpp:183
                R(Piece.PitfighterHarness, Tier.Specialist, "Pitfighter Harness", "Specialist", PgRecords.PitfighterHarness, null, null, 80, 80), // C++: ArenaRewards.cpp:184
                R(Piece.PitfighterHarness, Tier.Masterwork, "Pitfighter Harness", "Masterwork", PgRecords.PitfighterHarness, null, null, 95, 120), // C++: ArenaRewards.cpp:185
                R(Piece.PitfighterJawGuard, Tier.Standard, "Pitfighter Jaw Guard", "Standard", PgRecords.PitfighterJawGuard, null, null, 40, 20), // C++: ArenaRewards.cpp:188
                R(Piece.PitfighterJawGuard, Tier.HighGrade, "Pitfighter Jaw Guard", "High Grade", PgRecords.PitfighterJawGuard, null, null, 60, 40), // C++: ArenaRewards.cpp:189
                R(Piece.PitfighterJawGuard, Tier.Specialist, "Pitfighter Jaw Guard", "Specialist", PgRecords.PitfighterJawGuard, null, null, 80, 60), // C++: ArenaRewards.cpp:190
                R(Piece.PitfighterJawGuard, Tier.Masterwork, "Pitfighter Jaw Guard", "Masterwork", PgRecords.PitfighterJawGuard, null, null, 95, 80), // C++: ArenaRewards.cpp:191
                R(Piece.PitfighterPants, Tier.Standard, "Pitfighter Pants", "Standard", PgRecords.PitfighterPants, null, null, 40, 22), // C++: ArenaRewards.cpp:194
                R(Piece.PitfighterPants, Tier.HighGrade, "Pitfighter Pants", "High Grade", PgRecords.PitfighterPants, null, null, 60, 45), // C++: ArenaRewards.cpp:195
                R(Piece.PitfighterPants, Tier.Specialist, "Pitfighter Pants", "Specialist", PgRecords.PitfighterPants, null, null, 80, 70), // C++: ArenaRewards.cpp:196
                R(Piece.PitfighterPants, Tier.Masterwork, "Pitfighter Pants", "Masterwork", PgRecords.PitfighterPants, null, null, 95, 100), // C++: ArenaRewards.cpp:197
                R(Piece.RetainerPants, Tier.Standard, "Retainer Leather Pants", "Standard", PgRecords.RetainerLeatherPants, null, null, 40, 22), // C++: ArenaRewards.cpp:200
                R(Piece.RetainerPants, Tier.HighGrade, "Retainer Leather Pants", "High Grade", PgRecords.RetainerLeatherPants, null, null, 60, 45), // C++: ArenaRewards.cpp:201
                R(Piece.RetainerPants, Tier.Specialist, "Retainer Leather Pants", "Specialist", PgRecords.RetainerLeatherPants, null, null, 80, 70), // C++: ArenaRewards.cpp:202
                R(Piece.RetainerPants, Tier.Masterwork, "Retainer Leather Pants", "Masterwork", PgRecords.RetainerLeatherPants, null, null, 95, 100), // C++: ArenaRewards.cpp:203
                R(Piece.RetainerJacket, Tier.Standard, "Retainer Leather Jacket", "Standard", PgRecords.RetainerLeatherArmour, null, null, 40, 25), // C++: ArenaRewards.cpp:206
                R(Piece.RetainerJacket, Tier.HighGrade, "Retainer Leather Jacket", "High Grade", PgRecords.RetainerLeatherArmour, null, null, 60, 50), // C++: ArenaRewards.cpp:207
                R(Piece.RetainerJacket, Tier.Specialist, "Retainer Leather Jacket", "Specialist", PgRecords.RetainerLeatherArmour, null, null, 80, 80), // C++: ArenaRewards.cpp:208
                R(Piece.RetainerJacket, Tier.Masterwork, "Retainer Leather Jacket", "Masterwork", PgRecords.RetainerLeatherArmour, null, null, 95, 120), // C++: ArenaRewards.cpp:209
                R(Piece.RetainerBoots, Tier.Standard, "Retainer Boots", "Standard", PgRecords.RetainerLeatherBoots, null, null, 40, 20), // C++: ArenaRewards.cpp:212
                R(Piece.RetainerBoots, Tier.HighGrade, "Retainer Boots", "High Grade", PgRecords.RetainerLeatherBoots, null, null, 60, 40), // C++: ArenaRewards.cpp:213
                R(Piece.RetainerBoots, Tier.Specialist, "Retainer Boots", "Specialist", PgRecords.RetainerLeatherBoots, null, null, 80, 60), // C++: ArenaRewards.cpp:214
                R(Piece.RetainerBoots, Tier.Masterwork, "Retainer Boots", "Masterwork", PgRecords.RetainerLeatherBoots, null, null, 95, 80), // C++: ArenaRewards.cpp:215
                R(Piece.RetainerHelmet, Tier.Standard, "Retainer Leather Helmet", "Standard", PgRecords.RetainerLeatherHelmet, null, null, 40, 20), // C++: ArenaRewards.cpp:218
                R(Piece.RetainerHelmet, Tier.HighGrade, "Retainer Leather Helmet", "High Grade", PgRecords.RetainerLeatherHelmet, null, null, 60, 40), // C++: ArenaRewards.cpp:219
                R(Piece.RetainerHelmet, Tier.Specialist, "Retainer Leather Helmet", "Specialist", PgRecords.RetainerLeatherHelmet, null, null, 80, 60), // C++: ArenaRewards.cpp:220
                R(Piece.RetainerHelmet, Tier.Masterwork, "Retainer Leather Helmet", "Masterwork", PgRecords.RetainerLeatherHelmet, null, null, 95, 80), // C++: ArenaRewards.cpp:221
                R(Piece.EnforcerLegplates, Tier.Standard, "Enforcer Legplates", "Standard", PgRecords.EnforcerLegplates, null, null, 40, 30), // C++: ArenaRewards.cpp:224
                R(Piece.EnforcerLegplates, Tier.HighGrade, "Enforcer Legplates", "High Grade", PgRecords.EnforcerLegplates, null, null, 60, 60), // C++: ArenaRewards.cpp:225
                R(Piece.EnforcerLegplates, Tier.Specialist, "Enforcer Legplates", "Specialist", PgRecords.EnforcerLegplates, null, null, 80, 90), // C++: ArenaRewards.cpp:226
                R(Piece.EnforcerLegplates, Tier.Masterwork, "Enforcer Legplates", "Masterwork", PgRecords.EnforcerLegplates, null, null, 95, 120), // C++: ArenaRewards.cpp:227
                R(Piece.EnforcerBoots, Tier.Standard, "Enforcer Plated Boots", "Standard", PgRecords.EnforcerPlatedBoots, null, null, 40, 25), // C++: ArenaRewards.cpp:230
                R(Piece.EnforcerBoots, Tier.HighGrade, "Enforcer Plated Boots", "High Grade", PgRecords.EnforcerPlatedBoots, null, null, 60, 50), // C++: ArenaRewards.cpp:231
                R(Piece.EnforcerBoots, Tier.Specialist, "Enforcer Plated Boots", "Specialist", PgRecords.EnforcerPlatedBoots, null, null, 80, 75), // C++: ArenaRewards.cpp:232
                R(Piece.EnforcerBoots, Tier.Masterwork, "Enforcer Plated Boots", "Masterwork", PgRecords.EnforcerPlatedBoots, null, null, 95, 100), // C++: ArenaRewards.cpp:233
                R(Piece.EnforcerChest, Tier.Standard, "Plate Enforcer Chest", "Standard", PgRecords.EnforcerPlateChest, null, null, 40, 40), // C++: ArenaRewards.cpp:236
                R(Piece.EnforcerChest, Tier.HighGrade, "Plate Enforcer Chest", "High Grade", PgRecords.EnforcerPlateChest, null, null, 60, 80), // C++: ArenaRewards.cpp:237
                R(Piece.EnforcerChest, Tier.Specialist, "Plate Enforcer Chest", "Specialist", PgRecords.EnforcerPlateChest, null, null, 80, 120), // C++: ArenaRewards.cpp:238
                R(Piece.EnforcerChest, Tier.Masterwork, "Plate Enforcer Chest", "Masterwork", PgRecords.EnforcerPlateChest, null, null, 95, 160), // C++: ArenaRewards.cpp:239
                R(Piece.EnforcerHelmet, Tier.Standard, "Enforcer Helmet", "Standard", PgRecords.EnforcerHelmet, null, null, 40, 30), // C++: ArenaRewards.cpp:242
                R(Piece.EnforcerHelmet, Tier.HighGrade, "Enforcer Helmet", "High Grade", PgRecords.EnforcerHelmet, null, null, 60, 60), // C++: ArenaRewards.cpp:243
                R(Piece.EnforcerHelmet, Tier.Specialist, "Enforcer Helmet", "Specialist", PgRecords.EnforcerHelmet, null, null, 80, 90), // C++: ArenaRewards.cpp:244
                R(Piece.EnforcerHelmet, Tier.Masterwork, "Enforcer Helmet", "Masterwork", PgRecords.EnforcerHelmet, null, null, 95, 120), // C++: ArenaRewards.cpp:245
                R(Piece.WarlordLongsword, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:248
                R(Piece.WarlordLongsword, Tier.HighGrade, "Warlord Longsword", "Pit Cutter", PgRecords.WarlordLongsword, PgRecords.PitCutter, PgRecords.ArenaForge, 55, 40), // C++: ArenaRewards.cpp:249
                R(Piece.WarlordLongsword, Tier.Specialist, "Warlord Longsword", "Arena Talon", PgRecords.WarlordLongsword, PgRecords.ArenaTalon, PgRecords.ArenaForge, 75, 80), // C++: ArenaRewards.cpp:250
                R(Piece.WarlordLongsword, Tier.Masterwork, "Warlord Longsword", "Warlord's Talon", PgRecords.WarlordLongsword, PgRecords.WarlordsTalon, PgRecords.NullForged, 95, 120), // C++: ArenaRewards.cpp:251
                R(Piece.WarlordKatana, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:254
                R(Piece.WarlordKatana, Tier.HighGrade, "Warlord Katana", "Pit Fang", PgRecords.WarlordKatana, PgRecords.PitFang, PgRecords.ArenaForge, 55, 40), // C++: ArenaRewards.cpp:255
                R(Piece.WarlordKatana, Tier.Specialist, "Warlord Katana", "Arena Fang", PgRecords.WarlordKatana, PgRecords.ArenaFang, PgRecords.ArenaForge, 75, 80), // C++: ArenaRewards.cpp:256
                R(Piece.WarlordKatana, Tier.Masterwork, "Warlord Katana", "Warlord Fang", PgRecords.WarlordKatana, PgRecords.WarlordsFang, PgRecords.NullForged, 95, 120), // C++: ArenaRewards.cpp:257
                R(Piece.Mace, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:260
                R(Piece.Mace, Tier.HighGrade, "Warlord Mace", "Pit Breaker", PgRecords.WarlordMace, PgRecords.PitBreaker, PgRecords.ArenaForge, 55, 50), // C++: ArenaRewards.cpp:261
                R(Piece.Mace, Tier.Specialist, "Warlord Mace", "Arena Crusher", PgRecords.WarlordMace, PgRecords.ArenaCrusher, PgRecords.ArenaForge, 75, 100), // C++: ArenaRewards.cpp:262
                R(Piece.Mace, Tier.Masterwork, "Warlord Mace", "Warlord's Verdict", PgRecords.WarlordMace, PgRecords.WarlordsVerdict, PgRecords.NullForged, 95, 150), // C++: ArenaRewards.cpp:263
                R(Piece.Sabre, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:266
                R(Piece.Sabre, Tier.HighGrade, "Warlord Sabre", "Pit Cutter", PgRecords.WarlordSabre, PgRecords.PitCutter, PgRecords.ArenaForge, 55, 50), // C++: ArenaRewards.cpp:267
                R(Piece.Sabre, Tier.Specialist, "Warlord Sabre", "Arena Talon", PgRecords.WarlordSabre, PgRecords.ArenaTalon, PgRecords.ArenaForge, 75, 100), // C++: ArenaRewards.cpp:268
                R(Piece.Sabre, Tier.Masterwork, "Warlord Sabre", "Warlord's Talon", PgRecords.WarlordSabre, PgRecords.WarlordsTalon, PgRecords.NullForged, 95, 150), // C++: ArenaRewards.cpp:269
                R(Piece.Hacker, Tier.Standard, "", "", "", null, null, 0, 0), // C++: ArenaRewards.cpp:272
                R(Piece.Hacker, Tier.HighGrade, "Warlord Hacker", "Pit Cleaver", PgRecords.WarlordCleaver, PgRecords.PitReaper, PgRecords.ArenaForge, 55, 50), // C++: ArenaRewards.cpp:273
                R(Piece.Hacker, Tier.Specialist, "Warlord Hacker", "Arena Reaper", PgRecords.WarlordCleaver, PgRecords.ArenaReaper, PgRecords.ArenaForge, 75, 100), // C++: ArenaRewards.cpp:274
                R(Piece.Hacker, Tier.Masterwork, "Warlord Hacker", "Warlord's Ruin", PgRecords.WarlordCleaver, PgRecords.WarlordsRuin, PgRecords.NullForged, 95, 150), // C++: ArenaRewards.cpp:275
            };
            c.Rewards = new Reward[(int)Piece.Count, (int)Tier.MaxCount];
            for (int i = 0; i < rows.Length; ++i)
                c.Rewards[(int)rows[i].Piece, (int)rows[i].Tier] = rows[i];
            return c;
        }

        static GeneralReward G(GeneralItem item, string stableId, string name, string stringId, string? blueprint, int cost,
            int charges, bool repeatable, string? prerequisite, string? grants) => new GeneralReward
        {
            Item = item, StableId = stableId, Name = name, StringId = stringId, BlueprintResearchStringId = blueprint, Cost = cost,
            Charges = charges, Repeatable = repeatable, PrerequisiteId = prerequisite, GrantsUnlockId = grants,
        };

        static Reward R(Piece piece, Tier tier, string pieceName, string tierName, string stringId, string? model,
            string? manufacturer, int quality, int cost) => new Reward
        {
            Piece = piece, Tier = tier, PieceName = pieceName, TierName = tierName, StringId = stringId, ModelStringId = model,
            ManufacturerStringId = manufacturer, QualityLevel = quality, Cost = cost,
        };
    }

    // The real tables (shipped values until ApplyPriceOverrides stamps them).
    static readonly Catalogue g_catalogue = Catalogue.Shipped();

    // Test seam (no C++ counterpart): an override visible only to the calling flow.
    static readonly AsyncLocal<Catalogue?> s_test = new AsyncLocal<Catalogue?>();

    /// <summary>The catalogue every function reads: the test override in this flow, else the real tables.</summary>
    internal static Catalogue Current => s_test.Value ?? g_catalogue;

    /// <summary>True while a test catalogue is active in this flow (production never sets one).</summary>
    internal static bool HasTestOverride => s_test.Value != null;

    /// <summary>Test hook isolating BOTH globals: <paramref name="state"/> as PGConfig, a fresh shipped catalogue, then
    /// ApplyPriceOverrides once (what the original does at load). Disposing restores both, catalogue first.</summary>
    internal static IDisposable UseStampedForTest(PGConfig.State state)
    {
        IDisposable config = PGConfig.UseForTest(state);
        IDisposable catalogue = UseForTest(Catalogue.Shipped());
        ApplyPriceOverrides();
        return new Both(catalogue, config);
    }

    /// <summary>Test hook: <see cref="UseStampedForTest(PGConfig.State)"/> with a clone of the current PGConfig whose marks
    /// multiplier is <paramref name="multiplier"/>.</summary>
    internal static IDisposable UseStampedForTest(double multiplier)
    {
        PGConfig.State state = PGConfig.Current.Clone();
        state.Multiplier = multiplier;
        return UseStampedForTest(state);
    }

    sealed class Both : IDisposable
    {
        readonly IDisposable _first, _second;
        public Both(IDisposable first, IDisposable second) { _first = first; _second = second; }
        public void Dispose() { _first.Dispose(); _second.Dispose(); }
    }

    /// <summary>Test hook: <see cref="Current"/> is <paramref name="catalogue"/> in the calling flow until disposed, which restores
    /// what was there before. Use <c>using</c>; dispose nested overrides in reverse order.</summary>
    internal static IDisposable UseForTest(Catalogue catalogue)
    {
        Catalogue? previous = s_test.Value;
        s_test.Value = catalogue;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly Catalogue? _previous;
        bool _done;
        public Restore(Catalogue? previous) { _previous = previous; }
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s_test.Value = _previous;
        }
    }

    // C++: ArenaRewards.cpp:280-285
    public static int TiersFor(Piece piece)
    {
        if (piece < Piece.Greatsword || piece >= Piece.Count)
            return 0;
        return kTierForPiece[(int)piece];
    }

    // C++: ArenaRewards.cpp:287-292
    public static Tier FirstTier(Piece piece)
    {
        if (piece < Piece.Greatsword || piece >= Piece.Count)
            return Tier.MaxCount;
        return kFirstTierForPiece[(int)piece];
    }

    // C++: ArenaRewards.cpp:294-301
    public static bool IsValidTier(Piece piece, Tier tier)
    {
        if (piece < Piece.Greatsword || piece >= Piece.Count)
            return false;
        int first = (int)FirstTier(piece);
        int value = (int)tier;
        return value >= first && value < first + TiersFor(piece);
    }

    // C++: ArenaRewards.cpp:303-313
    public static bool IsWeapon(Piece piece)
    {
        return piece == Piece.Greatsword ||
            piece == Piece.Katana ||
            piece == Piece.Polearm ||
            piece == Piece.WarlordLongsword ||
            piece == Piece.WarlordKatana ||
            piece == Piece.Mace ||
            piece == Piece.Sabre ||
            piece == Piece.Hacker;
    }

    // C++: ArenaRewards.cpp:315-325
    public static ArmourSet SetFor(Piece piece)
    {
        if ((piece >= Piece.PitfighterFootwraps && piece <= Piece.PitfighterPants) ||
            (piece >= Piece.RetainerPants && piece <= Piece.RetainerHelmet))
            return ArmourSet.T1;
        if (piece >= Piece.EnforcerLegplates && piece <= Piece.EnforcerHelmet)
            return ArmourSet.T2;
        if (!IsWeapon(piece))
            return ArmourSet.T3;
        return ArmourSet.None;
    }

    // C++: ArenaRewards.cpp:327-338
    public static ArmourRow RowFor(Piece piece)
    {
        if (piece >= Piece.PitfighterFootwraps && piece <= Piece.PitfighterPants)
            return ArmourRow.Pitfighter;
        if (piece >= Piece.RetainerPants && piece <= Piece.RetainerHelmet)
            return ArmourRow.Retainer;
        if (piece >= Piece.EnforcerLegplates && piece <= Piece.EnforcerHelmet)
            return ArmourRow.Enforcer;
        if (!IsWeapon(piece))
            return ArmourRow.Warlord;
        return ArmourRow.Count;
    }

    // C++: ArenaRewards.cpp:340-354
    public static ArmourSet SetForRow(ArmourRow row)
    {
        switch (row)
        {
            case ArmourRow.Pitfighter:
            case ArmourRow.Retainer:
                return ArmourSet.T1;
            case ArmourRow.Enforcer:
                return ArmourSet.T2;
            case ArmourRow.Warlord:
                return ArmourSet.T3;
            default:
                return ArmourSet.None;
        }
    }

    // C++: ArenaRewards.cpp:356-361
    public static string StableId(Piece piece)
    {
        if (piece < Piece.Greatsword || piece >= Piece.Count)
            return "";
        return kStableIds[(int)piece];
    }

    // C++: ArenaRewards.cpp:363-370
    public static Reward Get(Piece piece, Tier tier)
    {
        if (piece < Piece.Greatsword || piece >= Piece.Count)
            piece = Piece.Greatsword;
        if (!IsValidTier(piece, tier))
            tier = FirstTier(piece);
        return Current.Rewards[(int)piece, (int)tier];
    }

    // C++: ArenaRewards.cpp:372-381
    public static Tier NextTier(int highestTier, Piece piece)
    {
        Tier first = FirstTier(piece);
        if (first == Tier.MaxCount)
            return Tier.MaxCount;
        if (highestTier < (int)first)
            return first;
        // highestTier + 1 at INT_MAX is signed overflow in C++ (MSVC wraps); C# wraps to int.MinValue, an invalid tier.
        Tier next = (Tier)unchecked(highestTier + 1);
        return IsValidTier(piece, next) ? next : Tier.MaxCount;
    }

    // C++: ArenaRewards.cpp:383-386
    public static bool IsComplete(int highestTier, Piece piece)
    {
        return NextTier(highestTier, piece) == Tier.MaxCount;
    }

    // C++: ArenaRewards.cpp:388-395
    public static bool CanClaim(int highestTier, Piece piece, Tier tier, int marks)
    {
        if (!IsValidTier(piece, tier))
            return false;
        if (NextTier(highestTier, piece) != tier)
            return false;
        return marks >= Get(piece, tier).Cost;
    }

    // C++: ArenaRewards.cpp:397-402
    public static Licence GetLicence(ArmourSet set)
    {
        if (set < ArmourSet.T1 || set > ArmourSet.T3)
            set = ArmourSet.T1;
        return Current.Licences[(int)set];
    }

    // C++: ArenaRewards.cpp:404-407
    public static Licence GetWeaponLicence()
    {
        return Current.WeaponLicence;
    }

    // C++: ArenaRewards.cpp:409-419
    public static bool CanPurchaseLicence(
        ArmourSet set,
        bool alreadyUnlocked,
        bool prerequisiteUnlocked,
        int marks)
    {
        if (set < ArmourSet.T1 || set > ArmourSet.T3 || alreadyUnlocked ||
            !prerequisiteUnlocked)
            return false;
        return marks >= GetLicence(set).Cost;
    }

    // C++: ArenaRewards.cpp:421-424
    public static bool CanPurchaseWeaponLicence(bool alreadyUnlocked, int marks)
    {
        return !alreadyUnlocked && marks >= Current.WeaponLicence.Cost;
    }

    // C++: ArenaRewards.cpp:426-431
    public static GeneralReward GetGeneral(GeneralItem item)
    {
        if (item < GeneralItem.ArenaBuildingsI || item >= GeneralItem.Count)
            item = GeneralItem.ArenaBuildingsI;
        return Current.GeneralRewards[(int)item];
    }

    /// <summary>Stamps the configured prices into this catalogue: every reward tier, every
    /// general item, and the licences. Called once by PGConfig::Load, because
    /// the rewards screen, the affordability gates, the ledger reservation and the
    /// half-price replace all read the stamped field rather than asking for a price. (ArenaRewards.h:141-145)</summary>
    // C++: ArenaRewards.cpp:433-455
    public static void ApplyPriceOverrides()
    {
        // port note (test guard, no C++ counterpart): a test that overrides PGConfig must also isolate the catalogue, or it would
        // stamp the process-wide tables for every other test. Production never sets PGConfig's test override, so this cannot fire.
        if (PGConfig.HasTestOverride && !HasTestOverride)
            throw new InvalidOperationException("ApplyPriceOverrides under a PGConfig test override needs a test catalogue (ArenaRewards.UseStampedForTest)");
        Catalogue c = Current;
        for (int index = 0; index < (int)Piece.Count; ++index)
        {
            Piece piece = (Piece)index;
            int first = (int)FirstTier(piece);
            int tiers = TiersFor(piece);
            for (int offset = 0; offset < tiers; ++offset)
            {
                int tier = first + offset;
                ref Reward reward = ref c.Rewards[index, tier];
                reward.Cost = PGConfig.GearCost(kStableIds[index], tier, reward.Cost);
            }
        }
        for (int item = 0; item < (int)GeneralItem.Count; ++item)
            c.GeneralRewards[item].Cost = PGConfig.SupplyCost(
                c.GeneralRewards[item].StableId, c.GeneralRewards[item].Cost);
        for (int set = 0; set < 3; ++set)
            c.Licences[set].Cost = PGConfig.LicenceCost(
                c.Licences[set].StableId, c.Licences[set].Cost);
        c.WeaponLicence.Cost = PGConfig.LicenceCost(
            c.WeaponLicence.StableId, c.WeaponLicence.Cost);
    }

    // C++: ArenaRewards.cpp:457-460
    public static bool IsGeneralVisible(GeneralItem item, bool arenaOnePurchased)
    {
        return item != GeneralItem.ArenaBuildingsII || arenaOnePurchased;
    }

    /// <summary>Half the catalog cost, rounded half-up (15 -> 8, 10 -> 5). (ArenaRewards.h:147)</summary>
    // C++: ArenaRewards.cpp:462-467
    public static int ReplaceCost(int fullCost)
    {
        if (fullCost <= 0)
            return 0;
        // fullCost + 1 at INT_MAX is signed overflow in C++ (MSVC wraps); C# wraps the same way.
        return unchecked(fullCost + 1) / 2;
    }

    /// <summary>Last claimed tier for a piece, or TierMaxCount if none claimed. (ArenaRewards.h:150)</summary>
    // C++: ArenaRewards.cpp:469-474
    public static Tier HighestClaimedTier(int highestTier, Piece piece)
    {
        return IsValidTier(piece, (Tier)highestTier)
            ? (Tier)highestTier
            : Tier.MaxCount;
    }

    // C++: ArenaRewards.cpp:476-522
    public static Offer ResolveOffer(
        int highestTier,
        Piece piece,
        bool hasExactPreviousGrade,
        bool hasAnyGradeOfPiece)
    {
        Offer offer;
        offer.Kind = OfferKind.Complete;
        offer.Tier = Tier.MaxCount;
        offer.Cost = 0;

        Tier next = NextTier(highestTier, piece);
        if (next == Tier.MaxCount)
        {
            if (hasAnyGradeOfPiece)
                return offer;
            Tier highest = HighestClaimedTier(highestTier, piece);
            if (highest == Tier.MaxCount)
                return offer;
            offer.Kind = OfferKind.Replace;
            offer.Tier = highest;
            offer.Cost = ReplaceCost(Get(piece, highest).Cost);
            return offer;
        }

        if (highestTier < (int)FirstTier(piece))
        {
            offer.Kind = OfferKind.Claim;
            offer.Tier = next;
            offer.Cost = Get(piece, next).Cost;
            return offer;
        }

        if (hasExactPreviousGrade)
        {
            offer.Kind = OfferKind.Claim;
            offer.Tier = next;
            offer.Cost = Get(piece, next).Cost;
            return offer;
        }

        Tier replaceTier = (Tier)(next - 1);
        offer.Kind = OfferKind.Replace;
        offer.Tier = replaceTier;
        offer.Cost = ReplaceCost(Get(piece, replaceTier).Cost);
        return offer;
    }
}
