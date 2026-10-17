using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Botja.Services;

internal enum ObstacleShape { Circle, Wedge }

// A single no-go shape for route planning: a full circle (Sound-type aggro, omnidirectional) or a
// facing wedge/pie-slice (Sight-type aggro, frontal cone only). Magic-type hostiles never become an
// obstacle at all (filtered out before this point).
internal readonly record struct PathObstacle(Vector2 Center, float Radius, float Facing, float HalfAngle, ObstacleShape Shape)
{
    public static PathObstacle Circle(Vector2 center, float radius) => new(center, radius, 0f, 0f, ObstacleShape.Circle);

    public static PathObstacle Wedge(Vector2 apex, float facing, float halfAngle, float range) =>
        new(apex, range, facing, halfAngle, ObstacleShape.Wedge);
}

// Grid/cost-map planner: rasterizes the local area around the route into cells, marks any cell
// truly inside an obstacle's real hard shape as impassable, and gives every OTHER cell a cost that
// rises near an obstacle (respecting the Sight wedge's actual cone — its side/back stays genuinely
// free) and falls to zero once clear, then runs A* over the grid. This replaces the earlier
// visibility-graph design, whose only candidate waypoints were ever exactly on an obstacle's
// boundary (plus a hand-added "comfort tier" of extra boundary points) — it had no real concept of
// open space at all. Here every open cell IS a candidate, so the search can actually discover and
// prefer genuinely clear routes instead of only ever hugging some obstacle's edge.
internal static class ObstaclePathPlanner
{
    private const float CellSizeYalms = 2f;
    // Extra room around the straight start->goal box so a real detour has somewhere to swing into —
    // scales with the largest nearby obstacle so a big Sound radius still gets room to go around.
    private const float BoundingBoxMarginYalms = 10f;
    // Hard cap on cell count so a pathological huge distance/margin can't allocate a huge grid —
    // resolution is coarsened (not the search skipped) if this would be exceeded.
    private const int MaxGridCells = 40_000;
    private const float ClearancePenaltyPerYalm = 2f;
    // Step used when sampling a segment against the (shape-aware, not polygon-approximated) obstacle
    // test — used only for the final shortcut/simplification pass, not the main grid search.
    private const float SegmentSampleStepYalms = 1f;
    // A candidate shortcut during string-pulling is only taken if it doesn't reintroduce more than
    // this much clearance cost — otherwise the safer (if more zig-zaggy) A* route it would have
    // replaced is kept instead of "optimizing" straight back into a tight squeeze.
    private const float MaxShortcutPenalty = 4f;
    // Below this, a hop is short enough that MergeShortHops considers folding it into its neighbor —
    // real repro reports: tightly-curving detours near an obstacle's comfort-zone boundary (where
    // SimplifyPath's farthest-shortcut search keeps failing the stricter MaxShortcutPenalty check)
    // can still leave a chain of several near-grid-cell-sized (~2y) hops in a row, which both looks
    // like clustering in the debug overlay and means several extra short StartMove/arrival cycles —
    // more chances for vnav's own arrival slop to matter.
    private const float MinMergeSpacingYalms = 3f;
    // Deliberately more lenient than MaxShortcutPenalty — safe specifically because MergeShortHops
    // only ever proposes this for an already-short span (both flanking hops below
    // MinMergeSpacingYalms), so any extra comfort-zone intrusion it accepts is brief, unlike
    // loosening the tolerance for a long shortcut would be. Still never overrides the hard
    // IsSegmentBlocked check — this only trades a little SOFT comfort margin for fewer hops.
    private const float MaxMergePenalty = 8f;

    private readonly record struct Grid(Vector2 Origin, int Width, int Height, float CellSize, bool[] Blocked, float[] Cost);

    // Returns the detour route (start -> ... -> goal) around every given obstacle, or null if no
    // safe route exists (the goal position is itself inside an obstacle's hard shape, or every
    // candidate route is blocked — genuinely boxed in). comfortDistanceYalms is how far beyond
    // an obstacle's own hard radius still counts as "worth avoiding when the extra distance is
    // cheap" — the user-tunable knob for how wide a berth the route prefers to give when it's free
    // to do so (HostileDetectionConfig.AvoidanceComfortDistanceYalms). realPathSpine, if given, is
    // vnav's own real terrain-following route (the "white line") from roughly here to the
    // destination — used only to shape the search area around the real route's curvature instead of
    // the idealized straight line, never as the literal move target (see FateNavigationService for
    // why steering to a real-path sub-node directly is a confirmed-bad idea).
    public static List<Vector3>? FindPath(Vector3 from, Vector3 to, IReadOnlyList<PathObstacle> obstacles, float comfortDistanceYalms, IReadOnlyList<Vector3>? realPathSpine = null)
    {
        Vector2 start = new(from.X, from.Z);
        Vector2 goal = new(to.X, to.Z);

        if (obstacles.Count == 0)
            return [from, to];

        if (IsPointBlocked(goal, obstacles))
            return null;

        // Standing right inside a hard-blocked zone already (e.g. a Sight mob turned into us, or we
        // were already close when it started being tracked) used to make this refuse to plan at all
        // — the grid's start CELL was impassable, so A* had nothing to search from. Escaping first
        // (push straight out of whatever's blocking, bounded passes for the rare case of standing in
        // an overlap of two) means we can still find a route out instead of just freezing in place
        // right in front of the thing we're trying to get away from.
        Vector2 planStart = start;
        bool escaped = false;
        if (IsPointBlocked(start, obstacles))
        {
            Vector2? escape = FindEscapePoint(start, obstacles);
            if (escape == null)
                return null; // genuinely enclosed on every side, nothing to do

            planStart = escape.Value;
            escaped = true;
        }

        Grid grid = BuildGrid(planStart, goal, obstacles, comfortDistanceYalms, realPathSpine);
        List<Vector2>? rawPath = AStar(grid, planStart, goal);
        if (rawPath == null)
            return null;

        // The raw grid result includes one point per cell it stepped through — collapse runs of
        // near-collinear cells down to only the genuinely necessary corners, same reasoning as
        // before: fewer waypoint transitions for the caller to drive through, and a far more stable
        // route across replans (small obstacle jitter no longer reshuffles a dozen near-identical
        // cell centers).
        List<Vector2> simplified = SimplifyPath(rawPath, obstacles, comfortDistanceYalms);

        // A narrower second pass specifically for chains of short hops SimplifyPath's stricter
        // shortcut search couldn't collapse (see MinMergeSpacingYalms/MaxMergePenalty above).
        simplified = MergeShortHops(simplified, obstacles, comfortDistanceYalms);

        // The escape leg (true start -> where the grid search actually began) is deliberately kept
        // OUT of simplification — it's not a "detour to optimize", it's the one required first step
        // of getting out of the blocked zone at all.
        if (escaped)
            simplified.Insert(0, start);

        // Every point (including the FINAL one, which is always exactly `goal`) interpolates its
        // elevation along the route instead of flat-copying the start height — the last point gets
        // exactly to.Y, matching the real destination's elevation, so the caller's own local
        // floor-snap has a correct starting guess instead of one that can be badly wrong on sloped
        // terrain (a real, confirmed bug in the previous design).
        return simplified.Select(p => new Vector3(p.X, InterpolateY(p, start, goal, from.Y, to.Y), p.Y)).ToList();
    }

    // Pushes straight away from whatever obstacle's center is blocking the point until it clears
    // that obstacle's hard radius — works for both Circle and Wedge alike, since IsPointBlockedByObstacle
    // always requires dist <= Radius as a first, shape-independent condition. Bounded passes handle
    // the rare case of standing in the overlap of two blocking obstacles at once.
    private static Vector2? FindEscapePoint(Vector2 point, IReadOnlyList<PathObstacle> obstacles)
    {
        const int maxPasses = 8;
        const float escapeMarginYalms = 0.5f;
        Vector2 current = point;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            PathObstacle? blocking = null;
            foreach (var obstacle in obstacles)
            {
                if (IsPointBlockedByObstacle(current, obstacle))
                {
                    blocking = obstacle;
                    break;
                }
            }

            if (blocking is not { } obstacleToEscape)
                return current;

            Vector2 offset = current - obstacleToEscape.Center;
            float dist = offset.Length();
            Vector2 direction = dist > 0.01f ? offset / dist : new Vector2(0f, 1f);
            current = obstacleToEscape.Center + (direction * (obstacleToEscape.Radius + escapeMarginYalms));
        }

        return IsPointBlocked(current, obstacles) ? null : current;
    }

    // Cheap per-tick check reused by the caller to decide whether a full replan is needed early
    // (e.g. a mob wandered/turned into the currently-followed path) without rebuilding the grid.
    public static bool IsSegmentBlocked(Vector3 from, Vector3 to, IReadOnlyList<PathObstacle> obstacles)
    {
        if (obstacles.Count == 0)
            return false;

        return IsSegmentBlocked(new Vector2(from.X, from.Z), new Vector2(to.X, to.Z), obstacles);
    }

    private static Grid BuildGrid(Vector2 start, Vector2 goal, IReadOnlyList<PathObstacle> obstacles, float comfortDistanceYalms, IReadOnlyList<Vector3>? realPathSpine)
    {
        float maxObstacleExtent = obstacles.Count > 0 ? obstacles.Max(o => o.Radius) : 0f;
        float margin = maxObstacleExtent + comfortDistanceYalms + BoundingBoxMarginYalms;

        // Bounding box covers every spine point (start, goal, and vnav's own real-path nodes if
        // given), not just the straight start->goal line — a real terrain-following route that bows
        // out around terrain the straight line never touches still gets proper search coverage, so a
        // detour can follow that same general shape instead of being squeezed into a narrower box an
        // idealized straight line would imply.
        float minX = MathF.Min(start.X, goal.X);
        float maxX = MathF.Max(start.X, goal.X);
        float minY = MathF.Min(start.Y, goal.Y);
        float maxY = MathF.Max(start.Y, goal.Y);
        if (realPathSpine != null)
        {
            foreach (var point in realPathSpine)
            {
                minX = MathF.Min(minX, point.X);
                maxX = MathF.Max(maxX, point.X);
                minY = MathF.Min(minY, point.Z);
                maxY = MathF.Max(maxY, point.Z);
            }
        }

        minX -= margin;
        maxX += margin;
        minY -= margin;
        maxY += margin;

        float cellSize = CellSizeYalms;
        int width = Math.Max(2, (int)MathF.Ceiling((maxX - minX) / cellSize));
        int height = Math.Max(2, (int)MathF.Ceiling((maxY - minY) / cellSize));

        if ((long)width * height > MaxGridCells)
        {
            float scale = MathF.Sqrt((float)((long)width * height) / MaxGridCells);
            cellSize *= scale;
            width = Math.Max(2, (int)MathF.Ceiling((maxX - minX) / cellSize));
            height = Math.Max(2, (int)MathF.Ceiling((maxY - minY) / cellSize));
        }

        var blocked = new bool[width * height];
        var cost = new float[width * height];
        Vector2 origin = new(minX, minY);

        for (int gy = 0; gy < height; gy++)
        {
            for (int gx = 0; gx < width; gx++)
            {
                Vector2 point = origin + new Vector2((gx + 0.5f) * cellSize, (gy + 0.5f) * cellSize);
                int idx = (gy * width) + gx;
                blocked[idx] = IsPointBlocked(point, obstacles);
                cost[idx] = blocked[idx] ? 0f : ObstacleCellCost(point, obstacles, comfortDistanceYalms);
            }
        }

        return new Grid(origin, width, height, cellSize, blocked, cost);
    }

    private static (int X, int Y) WorldToCell(Grid grid, Vector2 point) => (
        Math.Clamp((int)((point.X - grid.Origin.X) / grid.CellSize), 0, grid.Width - 1),
        Math.Clamp((int)((point.Y - grid.Origin.Y) / grid.CellSize), 0, grid.Height - 1));

    private static Vector2 CellToWorld(Grid grid, int x, int y) =>
        grid.Origin + new Vector2((x + 0.5f) * grid.CellSize, (y + 0.5f) * grid.CellSize);

    private static readonly (int Dx, int Dy)[] Neighbors8 =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    ];

    // Standard A* over the 8-connected grid — edge cost is real travel distance plus the traversed
    // cells' obstacle-proximity cost, so the search naturally prefers low-cost (open) cells over
    // high-cost (near-obstacle) ones whenever the detour is cheap. Every open cell is a genuine
    // candidate node, unlike the old visibility graph which only ever had nodes on obstacle boundaries.
    private static List<Vector2>? AStar(Grid grid, Vector2 start, Vector2 goal)
    {
        (int X, int Y) startCell = WorldToCell(grid, start);
        (int X, int Y) goalCell = WorldToCell(grid, goal);
        int startIdx = (startCell.Y * grid.Width) + startCell.X;
        int goalIdx = (goalCell.Y * grid.Width) + goalCell.X;

        if (grid.Blocked[startIdx] || grid.Blocked[goalIdx])
            return null; // shouldn't happen (already checked with the exact shape test), but be safe

        int n = grid.Width * grid.Height;
        var gScore = new float[n];
        var prev = new int[n];
        var visited = new bool[n];
        Array.Fill(gScore, float.MaxValue);
        Array.Fill(prev, -1);
        gScore[startIdx] = 0f;

        float Heuristic(int idx) => Vector2.Distance(CellToWorld(grid, idx % grid.Width, idx / grid.Width), goal);

        var queue = new PriorityQueue<int, float>();
        queue.Enqueue(startIdx, Heuristic(startIdx));

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            if (visited[current])
                continue;
            visited[current] = true;

            if (current == goalIdx)
                break;

            int cx = current % grid.Width, cy = current / grid.Width;
            foreach (var (dx, dy) in Neighbors8)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || nx >= grid.Width || ny < 0 || ny >= grid.Height)
                    continue;

                int nIdx = (ny * grid.Width) + nx;
                if (grid.Blocked[nIdx])
                    continue;

                // Prevents cutting through a diagonal gap between two blocked orthogonal neighbors —
                // the grid only guarantees each CELL CENTER is clear, so a diagonal step squeezed
                // between two blocked flanking cells can produce a straight-line segment that clips a
                // real obstacle boundary (round/angled, not grid-aligned) even though both endpoints
                // were open. Confirmed via a real repro log: the exact same diagonally-cut path got
                // computed and immediately re-flagged as blocked by the continuous check, over and
                // over, for 8+ minutes straight, with obstacles never even moving.
                if (dx != 0 && dy != 0)
                {
                    int flankA = (cy * grid.Width) + nx;
                    int flankB = (ny * grid.Width) + cx;
                    if (grid.Blocked[flankA] || grid.Blocked[flankB])
                        continue;
                }

                float stepDist = (dx != 0 && dy != 0) ? grid.CellSize * 1.41421356f : grid.CellSize;
                float avgCost = (grid.Cost[current] + grid.Cost[nIdx]) * 0.5f;
                float newG = gScore[current] + stepDist + (avgCost * (stepDist / grid.CellSize));

                if (newG < gScore[nIdx])
                {
                    gScore[nIdx] = newG;
                    prev[nIdx] = current;
                    queue.Enqueue(nIdx, newG + Heuristic(nIdx));
                }
            }
        }

        if (gScore[goalIdx] == float.MaxValue)
            return null;

        var cells = new List<int>();
        for (int at = goalIdx; at != -1; at = prev[at])
            cells.Add(at);
        cells.Reverse();

        var path = new List<Vector2> { start };
        foreach (int idx in cells)
            path.Add(CellToWorld(grid, idx % grid.Width, idx / grid.Width));
        path.Add(goal);
        return path;
    }

    private static float InterpolateY(Vector2 point, Vector2 start, Vector2 goal, float startY, float goalY)
    {
        Vector2 route = goal - start;
        float lengthSquared = route.LengthSquared();
        if (lengthSquared < 0.0001f)
            return startY;

        float t = Math.Clamp(Vector2.Dot(point - start, route) / lengthSquared, 0f, 1f);
        return startY + (goalY - startY) * t;
    }

    // Greedy shortcutting ("string pulling"): from the current point, jump straight to the FARTHEST
    // later point still reachable by a straight, unblocked, comfortably-clear line, instead of
    // visiting every intermediate cell center the grid search happened to step through.
    private static List<Vector2> SimplifyPath(List<Vector2> path, IReadOnlyList<PathObstacle> obstacles, float comfortDistanceYalms)
    {
        if (path.Count <= 2)
            return path;

        var result = new List<Vector2> { path[0] };
        int current = 0;
        while (current < path.Count - 1)
        {
            int next = current + 1;
            for (int candidate = path.Count - 1; candidate > current + 1; candidate--)
            {
                if (IsSegmentBlocked(path[current], path[candidate], obstacles))
                    continue;
                if (ClearancePenalty(path[current], path[candidate], obstacles, comfortDistanceYalms) > MaxShortcutPenalty)
                    continue;

                next = candidate;
                break;
            }

            result.Add(path[next]);
            current = next;
        }

        return result;
    }

    // Second, narrower pass over SimplifyPath's own output: folds a CHAIN of already-short hops
    // (both flanking segments below MinMergeSpacingYalms) into one longer segment, using the more
    // lenient MaxMergePenalty instead of MaxShortcutPenalty — never overrides the hard
    // IsSegmentBlocked check. Targets the specific case SimplifyPath's stricter farthest-shortcut
    // search structurally can't help with: several short hops in a row near an obstacle's comfort
    // boundary, each one individually too small a gain to justify the full shortcut tolerance, but
    // clustering into visible/behavioral clutter together.
    private static List<Vector2> MergeShortHops(List<Vector2> path, IReadOnlyList<PathObstacle> obstacles, float comfortDistanceYalms)
    {
        if (path.Count <= 2)
            return path;

        var result = new List<Vector2> { path[0] };
        int current = 0;
        while (current < path.Count - 1)
        {
            int next = current + 1;
            while (next < path.Count - 1
                && Vector2.Distance(path[current], path[next]) < MinMergeSpacingYalms
                && Vector2.Distance(path[next], path[next + 1]) < MinMergeSpacingYalms
                && !IsSegmentBlocked(path[current], path[next + 1], obstacles)
                && ClearancePenalty(path[current], path[next + 1], obstacles, comfortDistanceYalms) <= MaxMergePenalty)
            {
                next++;
            }

            result.Add(path[next]);
            current = next;
        }

        return result;
    }

    // Samples along the segment and checks each point against every obstacle's REAL shape (respects
    // the Sight wedge's actual cone, not a circle approximation) — simple and easy to verify, and
    // "light detours are fine" per the original design brief means the ~1y sampling step here doesn't
    // need to be exact analytic geometry the way the main grid's per-cell test does either.
    private static bool IsSegmentBlocked(Vector2 a, Vector2 b, IReadOnlyList<PathObstacle> obstacles)
    {
        float length = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)MathF.Ceiling(length / SegmentSampleStepYalms));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 point = Vector2.Lerp(a, b, (float)i / steps);
            if (IsPointBlocked(point, obstacles))
                return true;
        }

        return false;
    }

    // Peak (not summed) obstacle-proximity cost sampled along the segment — how deep the closest
    // approach cuts into anyone's comfort zone, used to gate string-pulling shortcuts.
    private static float ClearancePenalty(Vector2 a, Vector2 b, IReadOnlyList<PathObstacle> obstacles, float comfortDistanceYalms)
    {
        float length = Vector2.Distance(a, b);
        int steps = Math.Max(1, (int)MathF.Ceiling(length / SegmentSampleStepYalms));
        float maxCost = 0f;
        for (int i = 0; i <= steps; i++)
        {
            Vector2 point = Vector2.Lerp(a, b, (float)i / steps);
            maxCost = MathF.Max(maxCost, ObstacleCellCost(point, obstacles, comfortDistanceYalms));
        }

        return maxCost;
    }

    private static bool IsPointBlocked(Vector2 point, IReadOnlyList<PathObstacle> obstacles)
    {
        foreach (var obstacle in obstacles)
        {
            if (IsPointBlockedByObstacle(point, obstacle))
                return true;
        }

        return false;
    }

    private static bool IsPointBlockedByObstacle(Vector2 point, PathObstacle obstacle)
    {
        float dist = Vector2.Distance(point, obstacle.Center);
        if (dist > obstacle.Radius)
            return false;
        if (obstacle.Shape == ObstacleShape.Circle || dist <= 0.01f)
            return true; // a wedge's own apex (dist ~0) is unsafe from every direction regardless of facing

        Vector2 toPoint = (point - obstacle.Center) / dist;
        Vector2 forward = new(MathF.Sin(obstacle.Facing), MathF.Cos(obstacle.Facing));
        float cosAngle = Vector2.Dot(forward, toPoint);
        return cosAngle >= MathF.Cos(obstacle.HalfAngle);
    }

    private static float ObstacleCellCost(Vector2 point, IReadOnlyList<PathObstacle> obstacles, float comfortDistanceYalms)
    {
        float total = 0f;
        foreach (var obstacle in obstacles)
            total += ObstacleCostContribution(point, obstacle, comfortDistanceYalms);

        return total;
    }

    // Zero outside the comfort zone; for a Sight wedge, ALSO zero outside its actual cone — a mob's
    // side/back is genuinely free, not just "a bit less risky", so the search is free to hug it
    // closely there while still preferring to swing wide of its front.
    private static float ObstacleCostContribution(Vector2 point, PathObstacle obstacle, float comfortDistanceYalms)
    {
        float comfortRadius = obstacle.Radius + comfortDistanceYalms;
        float dist = Vector2.Distance(point, obstacle.Center);
        if (dist >= comfortRadius)
            return 0f;
        if (obstacle.Shape == ObstacleShape.Circle || dist <= 0.01f)
            return (comfortRadius - dist) * ClearancePenaltyPerYalm;

        Vector2 toPoint = (point - obstacle.Center) / dist;
        Vector2 forward = new(MathF.Sin(obstacle.Facing), MathF.Cos(obstacle.Facing));
        float cosAngle = Vector2.Dot(forward, toPoint);
        return cosAngle < MathF.Cos(obstacle.HalfAngle) ? 0f : (comfortRadius - dist) * ClearancePenaltyPerYalm;
    }
}

