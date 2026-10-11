using System.Collections.Generic;

namespace Botja.Services;

// The game never exposes a mob's real aggro type to the client — this is a hand-maintained lookup
// built from known Bozja/Zadnor mob identities (BNpcBase/BNpcName row id, i.e. IGameObject.BaseId,
// stable across every spawn/instance of the same mob template).
public enum AggroType
{
    // Frontal sight-cone aggro (the common case — used as the fallback for any unmapped DataId).
    Sight,
    // Omnidirectional proximity aggro — triggers on anything within a fixed short range regardless
    // of facing.
    Sound,
    // Never aggroes from travel proximity alone — fully ignored by route avoidance.
    Magic,
}

public static class MobAggroData
{
    // TODO: populate with real Bozja/Zadnor DataIds as they're identified in-game (see the
    // DataId/AggroType columns added to FateListWindow's hostile debug table). One plain id list per
    // non-default type — anything not listed in either falls through to the Sight default below.
    private static readonly HashSet<uint> SoundAggroMobs =  
    [12944,12922,12928,12964,12973,12935,12934,12916,12949,12943,12925,12955, // Zadnor
    11854,11801,11838,11857,11863,11871,11817,11816,11850,11874,11865]; //Southern front
    private static readonly HashSet<uint> MagicAggroMobs = [11815,11826,11827,11828,11836,11840,11845,11849,11860,11864,11869,11873,
    12701,12920,12924,12926,12927,12942,12947,12948,12959,12963,12972,12968]; // All Sprites

    public static AggroType Classify(uint dataId)
    {
        if (SoundAggroMobs.Contains(dataId))
            return AggroType.Sound;
        if (MagicAggroMobs.Contains(dataId))
            return AggroType.Magic;
        return AggroType.Sight;
    }
}
