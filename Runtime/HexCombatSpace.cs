using System;
using System.Collections.Generic;
using CupkekGames.HexGrids;
using CupkekGames.TimeSystem;
using UnityEngine;

namespace CupkekGames.Combat.HexGrids
{
    /// <summary>
    /// A fight on a hex field (<see cref="HexFieldView"/>): one unit per tile, distances in
    /// tiles, areas as tile patterns (a circle takes the tiles whose centre lies inside it, an
    /// arc the circle's tiles the way to its target, a line the tiles one wide the way to its
    /// target; warnings show exactly those tiles), and units that glide tile to tile
    /// (<see cref="HexCombatMover"/>).
    /// One combat unit is one tile. Every unit steps on fixed ticks: each frame this space
    /// runs the ticks the units' times made due, one tick per unit in turn, in the order they
    /// started, so the same fight plays out the same at any frame rate or speed.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class HexCombatSpace : CombatSpace
    {
        [Tooltip("The field the fight stands on; its tile size is the only world number.")]
        [SerializeField] private HexFieldView _field;

        [SerializeField, Min(1)] private int _ticksPerSecond = 20;

        [Tooltip("Seconds a unit takes to glide from one tile to the next.")]
        [SerializeField, Min(0.05f)] private float _secondsPerTile = 0.6f;

        [Tooltip("Tiles past an action's range that still count: 0.5 rounds a range to the nearest tile.")]
        [SerializeField, Min(0f)] private float _rangeTolerance = 0.5f;

        [Tooltip("The most ticks one frame may make due per unit; a longer hitch slows the fight instead.")]
        [SerializeField, Min(1)] private int _maxTicksPerFrame = 8;

        [Tooltip("The material area warnings are painted with (transparent, vertex-coloured, as a HexTilePainter's).")]
        [SerializeField] private Material _areaMaterial;

        [Tooltip("A warning's fill at full, as a share of its colour's alpha: the hexes read while the units on them still show.")]
        [SerializeField, Range(0f, 1f)] private float _areaFillOpacity = 0.4f;

        [Tooltip("The layer warnings are drawn on: a game whose ultimates darken the scene puts them on its effects layer, so they stay lit.")]
        [SerializeField] private string _areaLayer = "Default";

        // A position this close (flat metres) to a unit is that unit's tile, mid-glide or not.
        private const float SameSpot = 0.05f;

        private HexCombatBoard _board;
        private readonly Dictionary<CombatUnitView, HexCombatMover> _movers = new Dictionary<CombatUnitView, HexCombatMover>();
        private readonly Dictionary<int, HexCombatMover> _byId = new Dictionary<int, HexCombatMover>();
        private readonly List<HexCombatMover> _moverOrder = new List<HexCombatMover>();
        private readonly List<TickDrive> _drives = new List<TickDrive>();
        private readonly List<int> _ids = new List<int>();
        private readonly List<HexCoord> _tiles = new List<HexCoord>();
        private readonly List<HexAreaMark> _marks = new List<HexAreaMark>();
        private int _nextId = 1;

        /// <summary>The board's rules and state; built on first use from the field.</summary>
        public HexCombatBoard Board => _board ??= BuildBoard();

        public HexFieldView Field => _field;

        /// <summary>Every unit's mover, in the order they were made (fallen ones too, not <see cref="HexCombatMover.Placed"/>).</summary>
        public IReadOnlyList<HexCombatMover> Movers => _moverOrder;

        public float StepSeconds => 1f / _ticksPerSecond;

        /// <summary>A length in combat units as whole tiles, with the range tolerance.</summary>
        public int Tiles(float units) => Mathf.Max(0, Mathf.FloorToInt(units + _rangeTolerance));

        /// <summary>
        /// Hex steps between two units, plus a thousandth of their straight-line distance in
        /// tiles, so equal step counts break toward the unit that is truly nearer.
        /// </summary>
        public override float Distance(CombatUnit a, CombatUnit b)
        {
            int ia = IdOf(a);
            int ib = IdOf(b);
            if (ia == HexCombatBoard.NoTarget || ib == HexCombatBoard.NoTarget) return float.PositiveInfinity;
            float straight = Mathf.Sqrt(HexCoord.StraightDistanceSquared(Board.TileOf(ia), Board.TileOf(ib)));
            return Board.Distance(ia, ib) + 0.001f * straight;
        }

        /// <summary>A unit acts only standing on its tile: mid-glide nothing is in its range yet.</summary>
        public override bool InRange(CombatUnit caster, CombatUnit target, float range)
        {
            int ic = IdOf(caster);
            int it = IdOf(target);
            if (ic == HexCombatBoard.NoTarget || it == HexCombatBoard.NoTarget) return false;
            if (Board.IsStepping(ic)) return false;
            return Board.Distance(ic, it) <= range + _rangeTolerance;
        }

        /// <summary>
        /// Tiles to walk, around everyone in the way, before <paramref name="target"/> is within
        /// the reach a unit walks to for <paramref name="range"/> (at least the next tile): from
        /// the tile it stands on or is stepping to, to the target's. Off the field:
        /// <see cref="HexCombatBoard.Unreachable"/>.
        /// </summary>
        public override int StepsToReach(CombatUnit caster, CombatUnit target, float range)
        {
            int ic = IdOf(caster);
            int it = IdOf(target);
            if (ic == HexCombatBoard.NoTarget || it == HexCombatBoard.NoTarget) return HexCombatBoard.Unreachable;
            return Board.StepsToReach(ic, it, Reach(range));
        }

        /// <summary>The tiles a unit walks up to for an action of <paramref name="range"/>: at least the next one.</summary>
        public int Reach(float range) => Mathf.Max(1, Tiles(range));

        public override void Collect(in CombatArea area, List<CombatUnit> results)
        {
            results.Clear();
            TilesOf(area, _tiles);
            CollectOn(_tiles, results);
        }

        /// <summary>The units on the tiles within <paramref name="rings"/> steps of <paramref name="center"/>'s tile, it included, in id order.</summary>
        public override void CollectAround(CombatUnit center, int rings, List<CombatUnit> results)
        {
            results.Clear();
            if (!TryGetTile(center, out HexCoord tile)) return;

            Board.AroundTiles(tile, rings, _tiles);
            CollectOn(_tiles, results);
        }

        /// <summary>The units on the line one tile wide from <paramref name="from"/>'s tile through <paramref name="through"/>'s and on, <paramref name="length"/> tiles long, in id order.</summary>
        public override void CollectLine(CombatUnit from, CombatUnit through, int length, List<CombatUnit> results)
        {
            results.Clear();
            if (!TryGetTile(from, out HexCoord origin) || !TryGetTile(through, out HexCoord towards)) return;

            Board.LineTiles(origin, towards, length, _tiles);
            CollectOn(_tiles, results);
        }

        /// <summary>Pushes <paramref name="unit"/> away from <paramref name="from"/>'s tile on the board; its model slides to its new tile.</summary>
        public override int Push(CombatUnit unit, CombatUnit from, int tiles, float duration)
        {
            int id = IdOf(unit);
            if (id == HexCombatBoard.NoTarget || !TryGetTile(from, out HexCoord fromTile)) return 0;
            return _byId[id].Push(fromTile, tiles, duration);
        }

        private void CollectOn(List<HexCoord> tiles, List<CombatUnit> results)
        {
            Board.CollectOn(tiles, _ids);
            foreach (int id in _ids)
            {
                CombatUnit unit = _byId[id].View.CombatUnit;
                if (unit != null) results.Add(unit);
            }
        }

        /// <summary>
        /// The tiles an area covers: its origin and the point it aims at taken as tiles (a
        /// unit's own position is its tile, mid-glide or not), sizes in whole tiles. A line is
        /// one tile wide whatever its width.
        /// </summary>
        public void TilesOf(in CombatArea area, List<HexCoord> results)
        {
            HexCoord origin = OriginTile(area.Origin);
            switch (area.Shape)
            {
                case CombatAreaShape.Circle:
                    Board.CircleTiles(origin, area.Radius, results);
                    break;

                case CombatAreaShape.Arc:
                    Board.ArcTiles(origin, OriginTile(area.Towards), area.Radius, area.Angle, results);
                    break;

                case CombatAreaShape.Line:
                    Board.LineTiles(origin, OriginTile(area.Towards), Tiles(area.Length), results);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(area), area.Shape, "Unknown area shape.");
            }
        }

        private int AreaLayer()
        {
            int layer = LayerMask.NameToLayer(_areaLayer);
            if (layer < 0) throw new InvalidOperationException($"[HexCombatSpace] '{name}': the project has no '{_areaLayer}' layer for its warnings.");
            return layer;
        }

        public override CombatAreaMark ShowArea(in CombatArea area, Color color)
        {
            if (_areaMaterial == null)
            {
                throw new InvalidOperationException($"[HexCombatSpace] '{name}' has no area material; warnings are painted with it.");
            }

            HexAreaMark mark = null;
            foreach (HexAreaMark idle in _marks)
            {
                if (!idle.gameObject.activeSelf)
                {
                    mark = idle;
                    break;
                }
            }

            if (mark == null)
            {
                mark = HexAreaMark.Create(_field, _areaMaterial);
                mark.gameObject.layer = AreaLayer();
                _marks.Add(mark);
            }

            TilesOf(area, _tiles);
            mark.Show(_tiles, color, _areaFillOpacity);
            return mark;
        }

        /// <summary>A hazard tile (thorns): units walk around it and step off it while a safe tile serves.</summary>
        public bool IsHazard(HexCoord tile) => Board.IsHazard(tile);

        /// <summary>Makes <paramref name="tiles"/> the hazard tiles, replacing the last ones.</summary>
        public void SetHazards(IEnumerable<HexCoord> tiles) => Board.SetHazards(tiles);

        public override float ToWorld(float units) => units * Board.Layout.Spacing;

        public override ICombatMover CreateMover(CombatUnitView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));

            // A view set up again (a pooled body, a unit raised) starts over on the board.
            if (_movers.TryGetValue(view, out HexCombatMover previous))
            {
                previous.Halt();
                _movers.Remove(view);
                _byId.Remove(previous.Id);
                _moverOrder.Remove(previous);
            }

            var mover = new HexCombatMover(this, view, _nextId++);
            _movers[view] = mover;
            _byId[mover.Id] = mover;
            _moverOrder.Add(mover);
            mover.Place();
            return mover;
        }

        public override IDisposable Drive(TimeContext time, Action<float> step) => CreateDrive(time, step);

        internal TickDrive CreateDrive(TimeContext time, Action<float> step)
        {
            var drive = new TickDrive(time, step, _ticksPerSecond, _maxTicksPerFrame);
            _drives.Add(drive);
            return drive;
        }

        /// <summary>The board id of a unit standing on the field; <see cref="HexCombatBoard.NoTarget"/> if none.</summary>
        public int IdOf(CombatUnit unit)
        {
            if (unit?.View == null || !_movers.TryGetValue(unit.View, out HexCombatMover mover) || !mover.Placed)
            {
                return HexCombatBoard.NoTarget;
            }

            return mover.Id;
        }

        /// <summary>The unit standing on a tile, or null.</summary>
        public CombatUnit UnitOn(HexCoord tile) => Board.TryGetUnitAt(tile, out int id) ? _byId[id].View.CombatUnit : null;

        /// <summary>The tile a unit stands on; false when it is not on the field (lifted, fallen, never set up).</summary>
        public bool TryGetTile(CombatUnit unit, out HexCoord tile)
        {
            int id = IdOf(unit);
            tile = id == HexCombatBoard.NoTarget ? default : Board.TileOf(id);
            return id != HexCombatBoard.NoTarget;
        }

        /// <summary>Where a unit is: the tile it is stepping to, or the one it stands on; false when it is not on the field.</summary>
        public bool TryGetDestination(CombatUnit unit, out HexCoord tile)
        {
            int id = IdOf(unit);
            tile = id == HexCombatBoard.NoTarget ? default : Board.DestinationOf(id);
            return id != HexCombatBoard.NoTarget;
        }

        private void Awake()
        {
            if (_field == null)
            {
                throw new InvalidOperationException($"[HexCombatSpace] '{name}' has no HexFieldView; a fight on hexes needs its field.");
            }
        }

        private void Update()
        {
            // Every due tick, one per unit in turn, until none is left.
            float step = StepSeconds;
            bool ran;
            do
            {
                ran = false;
                int count = _drives.Count;
                for (int i = 0; i < count; i++)
                {
                    TickDrive drive = _drives[i];
                    if (drive.Disposed || !drive.Clock.Step()) continue;
                    drive.Invoke(step);
                    ran = true;
                }
            } while (ran);

            _drives.RemoveAll(d => d.Disposed);

            foreach (HexCombatMover mover in _moverOrder)
            {
                mover.Present();
            }
        }

        // A unit's own position means its tile, even mid-glide; any other point, the tile under it.
        private HexCoord OriginTile(Vector3 world)
        {
            foreach (HexCombatMover mover in _moverOrder)
            {
                if (!mover.Placed) continue;
                Vector3 offset = mover.View.transform.position - world;
                if (offset.x * offset.x + offset.z * offset.z <= SameSpot * SameSpot) return Board.TileOf(mover.Id);
            }

            return Board.Layout.ToHex(world);
        }

        private HexCombatBoard BuildBoard()
        {
            HexLayout layout = _field.Layout;
            int stepTicks = Mathf.Max(1, Mathf.RoundToInt(_secondsPerTile * _ticksPerSecond));
            return new HexCombatBoard(_field.Field, layout, stepTicks);
        }

        /// <summary>One unit's fixed ticks: its time feeds a clock that this space steps in turn.</summary>
        internal sealed class TickDrive : IDisposable
        {
            private TimeContext _time;
            private readonly Action<float> _step;

            public TickDrive(TimeContext time, Action<float> step, int ticksPerSecond, int maxTicksPerFrame)
            {
                _time = time ?? throw new ArgumentNullException(nameof(time));
                _step = step ?? throw new ArgumentNullException(nameof(step));
                Clock = new FixedStepClock(ticksPerSecond, maxTicksPerFrame);
                _time.OnUpdate += Clock.Accumulate;
            }

            public FixedStepClock Clock { get; }
            public bool Disposed => _time == null;

            public void Invoke(float seconds) => _step(seconds);

            public void Dispose()
            {
                if (_time == null) return;
                _time.OnUpdate -= Clock.Accumulate;
                _time = null;
            }
        }
    }
}
