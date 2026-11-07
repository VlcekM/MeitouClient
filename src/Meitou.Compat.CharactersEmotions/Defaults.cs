namespace Meitou.Compat.CharactersEmotions;

/// <summary>
/// The configuration the plugin writes when its INI files are missing, read from the plugin's embedded default-file text and its readmes
/// (docs/compat-characters-emotions.md). Used when the ported mod's <c>Plugins</c> folder has no INI file of its own.
/// </summary>
static class Defaults
{
    public const string Emotions = """
        [Master]
        InvalidShapeKey=Fcl_EYE_Close_R,Fcl_EYE_Close_L,Fcl_EYE_Close_H,Fcl_EYE_Joy_R,Fcl_EYE_Joy_L,Fcl_MTH_A,Fcl_MTH_I,Fcl_MTH_U,Fcl_MTH_E,Fcl_MTH_O,Fcl_Tongue_Depth,Fcl_Tongue_Height,Fcl_Tongue_Width,Fcl_Tongue_Length,Fcl_Tongue_C_Up,Fcl_Tongue_C_Down,Fcl_Tongue_Angle
        [Blinking]
        ShapeKey1=CE_Blinking
        ShapeKey1Amount=100.0
        [EmotionNormal1]
        ShapeKey1=CE_Normal1
        ShapeKey1Amount=100.0
        [EmotionNormal2]
        ShapeKey1=CE_Normal2
        ShapeKey1Amount=100.0
        [EmotionNormal3]
        ShapeKey1=CE_Normal3
        ShapeKey1Amount=100.0
        [EmotionAngry]
        ShapeKey1=CE_InCombat
        ShapeKey1Amount=100.0
        [EmotionSad]
        ShapeKey1=CE_Starving
        ShapeKey1Amount=100.0
        [EmotionPain]
        ShapeKey1=CE_Hurt
        ShapeKey1Amount=100.0
        [EmotionRelax]
        ShapeKey1=CE_Resting
        ShapeKey1Amount=100.0
        [EmotionSleep]
        ShapeKey1=CE_Sleeping
        ShapeKey1Amount=100.0
        [EmotionAttacked]
        ShapeKey1=CE_Damaged
        ShapeKey1Amount=100.0
        [EmotionKnockOut]
        ShapeKey1=CE_KnockOut
        ShapeKey1Amount=100.0
        """;

    public const string AnimEmotions = """
        [Master]
        RequirementShapeKey=CE_HardWorks1,CE_HardWorks2,CE_Stealth1,CE_Stealth2
        [stonemine]
        TotalAnimFrame=86
        EmotionPriority=3
        IgnoreBlink=False
        OnlyAnimEmotion=True
        ShapeKey1=CE_HardWorks1
        ShapeKey1Amount=100,100,0,100,100
        ShapeKey1Frame=0,15,30,60,86
        ShapeKey2=CE_HardWorks2
        ShapeKey2Amount=0,0,100,0,0
        ShapeKey2Frame=0,15,30,60,86
        [stealthKO]
        TotalAnimFrame=88
        EmotionPriority=3
        IgnoreBlink=False
        OnlyAnimEmotion=True
        ShapeKey1=CE_Stealth1
        ShapeKey1Amount=100,100,0,100,100
        ShapeKey1Frame=0,30,40,60,88
        ShapeKey2=CE_Stealth2
        ShapeKey2Amount=0,0,100,0,0
        ShapeKey2Frame=0,30,40,60,88
        [crouch idle,crouch walk,ninjarun]
        TotalAnimFrame=10
        EmotionPriority=3
        IgnoreBlink=False
        OnlyAnimEmotion=True
        ShapeKey1=CE_Stealth1
        ShapeKey1Amount=100,100
        ShapeKey1Frame=0,10
        """;
}
