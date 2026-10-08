using System;
using System.Collections.Generic;
using CupkekGames.HexGrids;
using UnityEngine;

namespace CupkekGames.Combat.HexGrids
{
    /// <summary>
    /// The rules of a fight on hexes, by integer id and without Unity objects: one unit per
    /// tile; a unit follows its target one tile at a time (it reserves the next tile, takes
    /// <see cref="StepTicks"/> ticks to glide there, then picks the next) until the target is
    /// within its reach; holding, rooted or without a target it stays. Hazard tiles (thorns)
    /// are walked around and never stopped on while a safe tile serves; a unit standing on
    /// one steps off. Areas are tile patterns. Everything walks pieces in id order and paths
    /// break ties the same way, so the same calls give the same fight.
    /// </summary>
    public sealed class HexCombatBoard
    {
        // A tile centre this close past a circle's radius (in tiles) still counts.
        private const float RadiusSlack = 1e-3f;

        private sealed class Piece
        {
            public int Id;
            public HexCoord Tile;
            public HexCoord To;
            public bool Stepping;
            public int Progress;
            public int Target = NoTarget;
            public int Reach = 1;
            public bool Holding;
            public bool Rooted;
        }

        public const int NoTarget = -1;

        /// <summary>What <see cref="StepsToReach"/> answers when no free way gets there.</summary>
        public const int Unreachable = int.MaxValue;

        private readonly HexField _field;
        private readonly HexLayout _layout;
        private readonly HexOccupancy _occupancy = new HexOccupancy();
        private readonly HexPathfinder _pathfinder = new HexPathfinder();
        private readonly List<Piece> _pieces = new List<Piece>();
        private readonly Dictionary<int, Piece> _byId = new Dictionary<int, Piece>();
        private readonly List<HexCoord> _hexes = new List<HexCoord>();
        private readonly List<HexCoord> _path = new List<HexCoord>();
        private readonly HashSet<HexCoord> _hazards = new HashSet<HexCoord>();
        private readonly HashSet<HexCoord> _tileSet = new HashSet<HexCoord>();

        public HexCombatBoard(HexField field, HexLayout layout, int stepTicks)
        {
            if (stepTicks < 1) throw new ArgumentOutOfRangeException(nameof(stepTicks), stepTicks, "A step takes at least one tick.");
            _field = field ?? throw new ArgumentNullException(nameof(field));
            _layout = layout;
            StepTicks = stepTicks;
        }

        public HexField Field => _field;
        public HexLayout Layout => _layout;

        /// <summary>Ticks one tile's glide takes.</summary>
        public int StepTicks { get; }

        public bool Contains(int id) => _byId.ContainsKey(id);

        /// <summary>The tile a unit stands on: the one it left until a step completes.</summary>
        public HexCoord TileOf(int id) => Get(id).Tile;

        /// <summary>Where a unit is: the tile it is stepping to, or the one it stands on.</summary>
        public HexCoord DestinationOf(int id) => Destination(Get(id));

        /// <summary>An open tile nobody stands on, steps to or has reserved.</summary>
        public bool IsFree(HexCoord tile) => _field.IsOpen(tile) && _occupancy.IsFree(tile, NoTarget);

        /// <summary>A hazard tile (thorns): walked around and never stopped on while a safe tile serves.</summary>
        public bool IsHazard(HexCoord tile) => _hazards.Contains(tile);

        /// <summary>Makes <paramref name="tiles"/> the hazard tiles, replacing the last ones.</summary>
        public void SetHazards(IEnumerable<HexCoord> tiles)
        {
            _hazards.Clear();
            foreach (HexCoord tile in tiles) _hazards.Add(tile);
        }

        /// <summary>The unit standing on a tile (the one it stands on until a step completes); false when nobody does.</summary>
        public bool TryGetUnitAt(HexCoord tile, out int id) => _occupancy.TryGetOccupant(tile, out id);

        /// <summary>
        /// Puts a unit on the free open tile nearest <paramref name="near"/> (rings outward,
        /// each ring in its fixed order). Returns the tile.
        /// </summary>
        public HexCoord Place(int id, HexCoord near)
        {
            if (_byId.ContainsKey(id)) throw new InvalidOperationException($"Unit {id} is already on the board at {_byId[id].Tile}.");

            int widest = HexCoord.Distance(near, _field.Center) + _field.Columns + _field.Rows;
            for (int radius = 0; radius <= widest; radius++)
            {
                HexShapes.Ring(near, radius, _hexes);
                foreach (HexCoord hex in _hexes)
                {
                    if (!_field.IsOpen(hex) || !_occupancy.IsFree(hex, id)) continue;

                    _occupancy.Place(id, hex);
                    var piece = new Piece { Id = id, Tile = hex, To = hex };
                    int index = 0;
                    while (index < _pieces.Count && _pieces[index].Id < id) index++;
                    _pieces.Insert(index, piece);
                    _byId[id] = piece;
                    return hex;
                }
            }

            throw new InvalidOperationException($"The board has no free tile left for unit {id}.");
        }

        /// <summary>Takes a unit off the board (it fell), freeing its tile and any tile it reserved.</summary>
        public void Remove(int id)
        {
            Piece piece = Get(id);
            _occupancy.Remove(id);
            _pieces.Remove(piece);
            _byId.Remove(id);
            foreach (Piece other in _pieces)
            {
                if (other.Target == id) other.Target = NoTarget;
            }
        }

        public int Distance(int a, int b) => HexCoord.Distance(Get(a).Tile, Get(b).Tile);

        /// <summary>
        /// The steps a unit still has to take before <paramref name="target"/> is within
        /// <paramref name="reach"/> tiles: measured from the tile it stands on or is stepping
        /// to, to the one the target stands on or is stepping to, around everyone in the way.
        /// 0 when it is within reach already; <see cref="Unreachable"/> when no free way gets
        /// there now.
        /// </summary>
        public int StepsToReach(int id, int target, int reach)
        {
            if (reach < 1) throw new ArgumentOutOfRangeException(nameof(reach), reach, "A reach is at least the next tile.");
            return TryFindWalk(Get(id), Get(target), reach) ? _path.Count : Unreachable;
        }

        public bool IsStepping(int id) => Get(id).Stepping;

        /// <summary>
        /// Whether a unit standing still sets off on its next tick: its target is out of its
        /// reach, or it stands on a hazard with a safe tile in reach (never while holding or
        /// rooted). Such a unit is not where it belongs yet.
        /// </summary>
        public bool WouldStep(int id)
        {
            Piece piece = Get(id);
            if (piece.Stepping || piece.Holding || piece.Rooted || piece.Target == NoTarget) return false;
            if (!_byId.TryGetValue(piece.Target, out Piece target)) return false;
            return TryFindWalk(piece, target, piece.Reach) && _path.Count > 0;
        }

        /// <summary>The step under way: from its tile to the one it reserved, ticks into it.</summary>
        public bool TryGetStep(int id, out HexCoord from, out HexCoord to, out int progress)
        {
            Piece piece = Get(id);
            from = piece.Tile;
            to = piece.To;
            progress = piece.Progress;
            return piece.Stepping;
        }

        public void Follow(int id, int target)
        {
            Piece piece = Get(id);
            if (target != NoTarget) Get(target);
            piece.Target = target;
            piece.Holding = false;
        }

        public void Hold(int id) => Get(id).Holding = true;

        public void SetReach(int id, int tiles)
        {
            if (tiles < 1) throw new ArgumentOutOfRangeException(nameof(tiles), tiles, "A reach is at least the next tile.");
            Get(id).Reach = tiles;
        }

        public void SetRooted(int id, bool rooted) => Get(id).Rooted = rooted;

        /// <summary>Completes a step under way at once (the unit stops or is pushed).</summary>
        public void Finish(int id)
        {
            Piece piece = Get(id);
            if (!piece.Stepping) return;

            _occupancy.CompleteMove(id);
            piece.Tile = piece.To;
            piece.Stepping = false;
            piece.Progress = 0;
        }

        /// <summary>
        /// One tick for one unit: a step under way moves on (and completes on its last tick,
        /// when the unit picks its next step at once, so a walk never pauses between tiles);
        /// otherwise the unit starts a step towards its target if it may. True while it moves.
        /// </summary>
        public bool Tick(int id)
        {
            Piece piece = Get(id);
            if (piece.Stepping)
            {
                piece.Progress++;
                if (piece.Progress < StepTicks) return true;
                Finish(id);
            }

            return TryStartStep(piece);
        }

        /// <summary>
        /// Dashes a unit towards <paramref name="worldEnd"/> along the line of tiles there: it
        /// stops before the first tile it cannot take, or with <paramref name="passOver"/> goes
        /// over taken tiles to the furthest free one (the edge of the field still stops it).
        /// Returns where it ends.
        /// </summary>
        public HexCoord Dash(int id, Vector3 worldEnd, bool passOver = false)
        {
            Finish(id);
            Piece piece = Get(id);
            HexShapes.Line(piece.Tile, _layout.ToHex(worldEnd), _hexes);
            HexCoord end = piece.Tile;
            for (int i = 1; i < _hexes.Count; i++)
            {
                HexCoord hex = _hexes[i];
                if (!_field.IsOpen(hex)) break;
                if (_occupancy.IsFree(hex, id)) end = hex;
                else if (!passOver) break;
            }

            if (end != piece.Tile)
            {
                _occupancy.TryReserve(id, end);
                _occupancy.CompleteMove(id);
                piece.Tile = end;
                piece.To = end;
            }

            return end;
        }

        /// <summary>
        /// The field's tiles whose centre lies within <paramref name="radius"/> tiles of
        /// <paramref name="origin"/>'s centre (a radius of 1.6 is the tile and its six
        /// neighbours).
        /// </summary>
        public void CircleTiles(HexCoord origin, float radius, List<HexCoord> results)
        {
            results.Clear();
            if (radius < 0f) return;

            HexShapes.Range(origin, Mathf.CeilToInt(radius), _hexes);
            float limit = (radius + RadiusSlack) * (radius + RadiusSlack);
            foreach (HexCoord hex in _hexes)
            {
                if (_field.Contains(hex) && HexCoord.StraightDistanceSquared(origin, hex) <= limit) results.Add(hex);
            }
        }

        /// <summary>
        /// The field's tiles of a circle (as <see cref="CircleTiles"/>) whose direction from
        /// <paramref name="origin"/> lies within half of <paramref name="angle"/> degrees of the
        /// way to <paramref name="towards"/>; the origin is left out. Nothing when the two meet.
        /// </summary>
        public void ArcTiles(HexCoord origin, HexCoord towards, float radius, float angle, List<HexCoord> results)
        {
            results.Clear();
            if (towards == origin || radius < 0f) return;

            HexShapes.Cone(origin, towards, Mathf.CeilToInt(radius), angle * 0.5f, _hexes);
            float limit = (radius + RadiusSlack) * (radius + RadiusSlack);
            foreach (HexCoord hex in _hexes)
            {
                if (_field.Contains(hex) && HexCoord.StraightDistanceSquared(origin, hex) <= limit) results.Add(hex);
            }
        }

        /// <summary>
        /// The field's tiles of a line one tile wide from <paramref name="origin"/> the way to
        /// <paramref name="towards"/> and on past it, <paramref name="length"/> tiles long; the
        /// origin is left out. Nothing when the two meet.
        /// </summary>
        public void LineTiles(HexCoord origin, HexCoord towards, int length, List<HexCoord> results)
        {
            results.Clear();
            int distance = HexCoord.Distance(origin, towards);
            if (distance == 0 || length < 1) return;

            // Hex distance scales along a straight line, so the far end is the way scaled to the length.
            double scale = (double)length / distance;
            HexCoord far = HexCoord.Round(origin.Q + (towards.Q - origin.Q) * scale, origin.R + (towards.R - origin.R) * scale);
            HexShapes.Line(origin, far, _hexes);
            for (int i = 1; i < _hexes.Count; i++)
            {
                if (_field.Contains(_hexes[i])) results.Add(_hexes[i]);
            }
        }

        /// <summary>The units standing on <paramref name="tiles"/> (the tile each stands on until a step completes), in id order.</summary>
        public void CollectOn(List<HexCoord> tiles, List<int> results)
        {
            results.Clear();
            _tileSet.Clear();
            foreach (HexCoord tile in tiles) _tileSet.Add(tile);
            foreach (Piece piece in _pieces)
            {
                if (_tileSet.Contains(piece.Tile)) results.Add(piece.Id);
            }
        }

        private bool TryStartStep(Piece piece)
        {
            if (piece.Holding || piece.Rooted || piece.Target == NoTarget) return false;
            if (!_byId.TryGetValue(piece.Target, out Piece target)) return false;

            // Chase where the target is going: two units walking at each other meet in the
            // middle instead of side-stepping around the tiles they are leaving.
            if (!TryFindWalk(piece, target, piece.Reach) || _path.Count == 0 || !_occupancy.TryReserve(piece.Id, _path[0])) return false;

            piece.To = _path[0];
            piece.Stepping = true;
            piece.Progress = 0;
            return true;
        }

        // The shortest walk for a piece, from where it stands or is stepping to, until the
        // tile its target stands on or is stepping to is within reach; into _path. It keeps
        // off hazards and ends on a safe tile while one serves; when none does, it walks as
        // if there were none (a field all thorns is fought on where it stands).
        private bool TryFindWalk(Piece piece, Piece target, int reach)
        {
            int id = piece.Id;
            HexCoord from = Destination(piece);
            HexCoord goal = Destination(target);
            if (_hazards.Count > 0 && _pathfinder.TryFindPath(_field, from, goal, reach,
                    hex => _occupancy.IsFree(hex, id) && !_hazards.Contains(hex),
                    hex => !_hazards.Contains(hex),
                    _path))
            {
                return true;
            }

            return _pathfinder.TryFindPath(_field, from, goal, reach, hex => _occupancy.IsFree(hex, id), _path);
        }

        private static HexCoord Destination(Piece piece) => piece.Stepping ? piece.To : piece.Tile;

        private Piece Get(int id)
        {
            if (!_byId.TryGetValue(id, out Piece piece)) throw new KeyNotFoundException($"Unit {id} is not on the board.");
            return piece;
        }

    }
}
