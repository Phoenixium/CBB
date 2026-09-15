using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

// Which of a Bozja region's 3 sub-zones a position falls in.
// Southern Front (920): Sector1 = southmost band, Sector3 = northmost band.
// Zadnor (975): Sector1 = Southern Plateau, Sector2 = Western Plateau, Sector3 = Northern Plateau.
public enum FateSector
{
    Sector1 = 1,
    Sector2 = 2,
    Sector3 = 3,
}

// Ranks active Bozja FATEs for auto-navigation: progress, time remaining, travel cost,
// how fast progress is accumulating (a proxy for "other players are actively working this"),
// and an optional bonus for a user-preferred map sector.
public class FatePriorityService(
    FateNavigationService nav,
    IClientState clientState,
    FatePriorityConfig config,
    BlacklistConfig blacklist
)
{
    // Southern Front (920) sectors are stacked north-to-south, and the aethernet shards sit right at
    // the checkpoints between them — confirmed against the in-game map.
    private const float SouthernFrontBoundary1Z = 192.278f; // Camp Steva
    private const float SouthernFrontBoundary2Z = 532.845f; // avg(Olana's Stand, Lunya's Stand)

    // Zadnor (975): Northern/Western/Southern Plateau meet at a single tripoint, and each pairwise
    // border is marked by an aethernet shard — confirmed against the in-game map. Classify by angle
    // around the tripoint's centroid rather than a simple axis band.
    private static readonly Vector2 ZadnorLjebanPoint = new(-689.387f, -292.164f);   // Northern/Western border
    private static readonly Vector2 ZadnorHrmovirPoint = new(106.370f, -130.815f);   // Northern/Southern border
    private static readonly Vector2 ZadnorZuprtikPoint = new(-356.466f, 758.449f);   // Western/Southern border
    private static readonly Vector2 ZadnorTripoint = (ZadnorLjebanPoint + ZadnorHrmovirPoint + ZadnorZuprtikPoint) / 3f;
    private static readonly float ZadnorZuprtikAngle = AngleAroundTripoint(ZadnorZuprtikPoint);
    private static readonly float ZadnorLjebanAngle = AngleAroundTripoint(ZadnorLjebanPoint);
    private static readonly float ZadnorHrmovirAngle = AngleAroundTripoint(ZadnorHrmovirPoint);

    // Normalization scales so default weights of ~1 have comparable influence on the score.
    private const float TimeRemainingNormSeconds = 12f;  // 1200s (20 min) -> 100
    private const float TravelTimeNormCap = 100f;         // cap travel penalty at 100s
    private const float ProgressRateNormScale = 1000f;    // %/sec -> a small, comparable bonus

    public float GetPriorityScore(IFate fate)
    {
        float progress = fate.Progress;
        float timeRemaining = fate.TimeRemaining;
        float travelSeconds = nav.EstimateTravelSeconds(fate.Position);

        if (config.ExcludeUnreachable && travelSeconds > timeRemaining)
            return float.NegativeInfinity;

        float elapsed = System.Math.Max(1f, fate.Duration - fate.TimeRemaining);
        float progressRate = progress / elapsed;

        float score = config.ProgressWeight * progress
                    + config.TimeRemainingWeight * System.Math.Clamp(timeRemaining / TimeRemainingNormSeconds, 0f, 100f)
                    + config.TravelTimeWeight * System.Math.Clamp(travelSeconds, 0f, TravelTimeNormCap)
                    + config.ProgressRateWeight * progressRate * ProgressRateNormScale;

        if (config.PreferredSector != FateSectorPreference.Any && GetSector(fate.Position) == (FateSector)config.PreferredSector)
            score += config.PreferredSectorBonus;

        return score;
    }

    public FateSector GetSector(Vector3 position) => (ushort)clientState.TerritoryType switch
    {
        920 => GetSouthernFrontSector(position),
        975 => GetZadnorSector(position),
        _ => FateSector.Sector2,
    };

    // Numbered south-to-north (Sector1 = southmost band, Sector3 = northmost).
    private static FateSector GetSouthernFrontSector(Vector3 position)
    {
        if (position.Z >= SouthernFrontBoundary2Z) return FateSector.Sector1;
        if (position.Z >= SouthernFrontBoundary1Z) return FateSector.Sector2;
        return FateSector.Sector3;
    }

    // Sector1 = Southern Plateau, Sector2 = Western Plateau, Sector3 = Northern Plateau — split by
    // angle around the 3 plateaus' shared tripoint, using each border checkpoint as an angular boundary.
    private static FateSector GetZadnorSector(Vector3 position)
    {
        float angle = AngleAroundTripoint(new Vector2(position.X, position.Z));

        if (IsAngleBetween(angle, ZadnorZuprtikAngle, ZadnorLjebanAngle)) return FateSector.Sector2; // Western Plateau
        if (IsAngleBetween(angle, ZadnorLjebanAngle, ZadnorHrmovirAngle)) return FateSector.Sector3; // Northern Plateau
        return FateSector.Sector1; // Southern Plateau (wraps through Hrmovir -> Zuprtik)
    }

    private static float AngleAroundTripoint(Vector2 point)
    {
        float degrees = System.MathF.Atan2(point.Y - ZadnorTripoint.Y, point.X - ZadnorTripoint.X) * (180f / System.MathF.PI);
        return degrees < 0f ? degrees + 360f : degrees;
    }

    // True if `angle` lies on the counter-clockwise arc from `from` to `to` (handles wraparound past 360°).
    private static bool IsAngleBetween(float angle, float from, float to) =>
        from <= to ? angle >= from && angle < to : angle >= from || angle < to;

    // Highest priority first; unreachable FATEs (when ExcludeUnreachable is on) sink to the bottom.
    public IEnumerable<IFate> GetSortedFates(IEnumerable<IFate> fates) =>
        fates.Where(fate => !IsBlacklisted(fate)).OrderByDescending(GetPriorityScore);

    // The stable FATE template ID (shared by every spawn of the same FATE, unlike the per-spawn
    // runtime FateId) — used as the blacklist key so a blacklisted FATE stays blacklisted across respawns.
    public uint GetFateTemplateId(IFate fate) => fate.GameData.RowId;

    public bool IsBlacklisted(IFate fate) => blacklist.FateIds.Contains(GetFateTemplateId(fate));

    public void Blacklist(IFate fate) => blacklist.FateIds.Add(GetFateTemplateId(fate));
}
