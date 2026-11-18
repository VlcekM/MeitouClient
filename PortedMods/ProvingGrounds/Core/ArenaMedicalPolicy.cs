// Port of upstream/ProvingGrounds/plugin/src/ArenaMedicalPolicy.cpp and the plain type it needs from ArenaMedical.h (Proving Grounds
// by Shutori, GPLv3). The rest of ArenaMedical.h/.cpp (SetProtocol/GetProtocol, the aid-need and stabilise calls, the hooks) is glue
// (src/Game/ArenaMedical.cs, M3), which will use the Protocol enum below.
namespace ProvingGrounds.Core;

/// <summary>Treatment rules of the two medical protocols. C++: namespace ArenaMedical, the pure part (ArenaMedicalPolicy.cpp; header
/// "Pure policy surface, kept separate from the hook details for easy testing", ArenaMedical.h:13) (PA §2.11). Named
/// ArenaMedicalPolicy here, after its file, so the glue class ArenaMedical keeps the C++ name.</summary>
public static class ArenaMedicalPolicy
{
    /// <summary>C++: ArenaMedical.h:7-11</summary>
    public enum Protocol
    {
        RingsideAid = 1,
        Bloodsport = 2
    }

    // C++: ArenaMedicalPolicy.cpp:5-16
    public static bool ShouldBlockTreatment(
        Protocol protocol,
        bool matchActive,
        bool targetIsParticipant,
        bool targetIsEliminated)
    {
        if (!matchActive || !targetIsParticipant)
            return false;
        if (protocol == Protocol.Bloodsport)
            return true;
        return !targetIsEliminated;
    }

    // C++: ArenaMedicalPolicy.cpp:18-21
    public static bool ShouldStabilizePrisoner(Protocol protocol)
    {
        return protocol != Protocol.Bloodsport;
    }

    // C++: ArenaMedicalPolicy.cpp:23-31
    public static string GetProtocolName(Protocol protocol)
    {
        switch (protocol)
        {
            case Protocol.Bloodsport: return "Bloodsport";
            case Protocol.RingsideAid:
            default: return "Ringside Aid";
        }
    }

    // C++: ArenaMedicalPolicy.cpp:33-38
    public static Protocol NextProtocol(Protocol protocol)
    {
        if (protocol == Protocol.RingsideAid)
            return Protocol.Bloodsport;
        return Protocol.RingsideAid;
    }
}
