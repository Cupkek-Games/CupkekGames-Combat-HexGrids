using System;
using System.Threading;
using CupkekGames.Animations;
using CupkekGames.HexGrids;
using CupkekGames.TimeSystem;
using PrimeTween;
using UnityEngine;
using UnityEngine.AI;

namespace CupkekGames.Combat.HexGrids
{
    /// <summary>
    /// One unit on a <see cref="HexCombatSpace"/>: the board decides its tile on each tick;
    /// between ticks the model glides from tile to tile, playing its
    /// <see cref="CombatUnitView.MoveAnimationKind"/>, and turns to face where it walks or,
    /// standing, its target; while it acts it faces the tile its target stood on when the
    /// action began, so its aim holds. A navmesh agent on the same body is switched off: the
    /// grid moves it.
    /// </summary>
    public sealed class HexCombatMover : ICombatMover
    {
        private const float TurnDegreesPerSecond = 720f;

        private readonly HexCombatSpace _space;
        private readonly CombatUnitView _view;
        private readonly Transform _transform;
        private TimeBundle _timeBundle;
        private HexCombatSpace.TickDrive _drive;
        private CombatUnit _target;
        private int _reach = 1;
        private bool _rooted;
        private bool _moving;
        // While acting: the tile its target stood on when the action began.
        private HexCoord? _aim;
        private Tween _dash;
        private CancellationTokenRegistration _dashCancel;

        internal HexCombatMover(HexCombatSpace space, CombatUnitView view, int id)
        {
            _space = space;
            _view = view;
            _transform = view.transform;
            Id = id;

            if (view.TryGetComponent(out NavMeshAgent agent)) agent.enabled = false;
        }

        /// <summary>The unit's id on the board.</summary>
        public int Id { get; }

        public CombatUnitView View => _view;

        /// <summary>On the board (false once it fell, until it starts again).</summary>
        public bool Placed { get; private set; }

        /// <summary>Picked up by <see cref="Lift"/> and not set down yet.</summary>
        public bool Lifted { get; private set; }

        private HexCombatBoard Board => _space.Board;

        /// <summary>
        /// On its tile, not gliding, not pushed, and not about to set off (its target out of
        /// reach, or thorns under it with a safe tile in reach): only then does it act.
        /// </summary>
        public bool IsSettled => Placed && !Board.IsStepping(Id) && !Dashing && !Board.WouldStep(Id);

        /// <summary>
        /// Mid-dash: the board has the unit on its new tile already, and its model is on the
        /// way there (up to the dash's length off its tile).
        /// </summary>
        public bool Dashing => _dash.isAlive;

        /// <summary>The tile the unit stands on, or the one it is stepping to.</summary>
        public HexCoord Tile => Board.DestinationOf(Id);

        public void Start(TimeBundle timeBundle)
        {
            if (Lifted) throw new InvalidOperationException($"[HexCombatMover] '{_view.name}' starts while lifted; set it down before its fight starts.");
            _timeBundle = timeBundle;
            if (!Placed) Place();
            _drive?.Dispose();
            _drive = _space.CreateDrive(timeBundle.TimeContext, OnTick);
        }

        /// <summary>
        /// Stops stepping (a pause, a stun, the gap between waves): the unit holds still where
        /// it is, mid-glide or not, and walks on from there when it starts again.
        /// </summary>
        public void Stop()
        {
            _drive?.Dispose();
            _drive = null;
            SetMoving(false);
        }

        public void SetReach(float range)
        {
            _reach = _space.Reach(range);
            if (Placed) Board.SetReach(Id, _reach);
        }

        public void Follow(CombatUnit target)
        {
            _target = target;
            _aim = null;
            if (Placed) Board.Follow(Id, _space.IdOf(target));
        }

        public void Hold()
        {
            if (!Placed) return;

            Board.Hold(Id);
            if (!_aim.HasValue && _space.TryGetTile(_target, out HexCoord tile)) _aim = tile;
        }

        public void Halt()
        {
            StopDash();
            _moving = false;
            if (!Placed) return;

            Board.Remove(Id);
            Placed = false;
        }

        /// <summary>Picks the unit up off the board, freeing its tile; only while its fight is not running.</summary>
        public void Lift()
        {
            if (!Placed) throw new InvalidOperationException($"[HexCombatMover] '{_view.name}' is not on the field; it cannot be lifted.");
            if (_drive != null) throw new InvalidOperationException($"[HexCombatMover] '{_view.name}' is fighting; lift only before Start or after Stop.");

            StopDash();
            SetMoving(false);
            Board.Remove(Id);
            Placed = false;
            Lifted = true;
        }

        /// <summary>
        /// Sets a lifted unit down on the free tile under <paramref name="world"/>, at its centre
        /// and at <paramref name="world"/>'s height; throws on a tile that is off the field,
        /// blocked or taken (lift the occupant first).
        /// </summary>
        public void SetDown(Vector3 world)
        {
            if (!Lifted) throw new InvalidOperationException($"[HexCombatMover] '{_view.name}' was not lifted.");

            HexCoord hex = Board.Layout.ToHex(world);
            if (!Board.IsFree(hex)) throw new InvalidOperationException($"[HexCombatMover] '{_view.name}' cannot stand on {hex}: off the field, blocked or taken. Lift the occupant first.");

            Vector3 at = Board.Layout.ToWorld(hex);
            at.y = world.y;
            _transform.position = at;
            Lifted = false;
            Place();
        }

        public void SetRooted(bool rooted)
        {
            _rooted = rooted;
            if (Placed) Board.SetRooted(Id, rooted);
        }

        /// <summary>
        /// Dashes the unit along <paramref name="offset"/> (combat units, so tiles). Push and
        /// Through take the new tile on the board at once (Push stops before a taken tile,
        /// Through goes over taken ones to the furthest free tile) and the model follows over
        /// <paramref name="duration"/>; Cosmetic moves only the model, out and back, and the
        /// unit keeps its tile.
        /// </summary>
        public void Dash(Vector3 offset, float duration, Ease ease, CombatDashMode mode, CancellationToken cancellationToken)
        {
            if (!Placed) return;

            StopDash();
            Vector3 start = _transform.position;
            Vector3 reach = start + offset * Board.Layout.Spacing;
            SetMoving(false);

            if (mode == CombatDashMode.Cosmetic)
            {
                _dash = Tween.Position(_transform, reach, Mathf.Max(0.01f, duration * 0.5f), ease, cycles: 2, cycleMode: CycleMode.Yoyo)
                    .OnComplete(() => _dashCancel.Dispose());
            }
            else
            {
                HexCoord end = Board.Dash(Id, reach, passOver: mode == CombatDashMode.Through);
                _dash = Tween.Position(_transform, TileCentre(end), Mathf.Max(0.01f, duration), ease)
                    .OnComplete(() => _dashCancel.Dispose());
            }

            _timeBundle?.TimeScaleTween.Add(_dash);
            _dashCancel = cancellationToken.Register(() =>
            {
                StopDash();
                if (Placed) SnapToTile();
            });
        }

        /// <summary>
        /// Pushes the unit away from <paramref name="from"/> (<see cref="HexCombatBoard.Push"/>):
        /// the board has it on its new tile at once and the model slides there over
        /// <paramref name="duration"/>. Returns the tiles it moved.
        /// </summary>
        internal int Push(HexCoord from, int tiles, float duration)
        {
            if (!Placed) return 0;

            StopDash();
            SetMoving(false);
            int moved = Board.Push(Id, from, tiles);
            if (moved == 0) return 0;

            _dash = Tween.Position(_transform, TileCentre(Board.TileOf(Id)), Mathf.Max(0.01f, duration), Ease.OutQuad);
            _timeBundle?.TimeScaleTween.Add(_dash);
            return moved;
        }

        internal void Place()
        {
            Board.Place(Id, Board.Layout.ToHex(_transform.position));
            Placed = true;
            Board.SetReach(Id, _reach);
            Board.SetRooted(Id, _rooted);
            if (_target != null) Board.Follow(Id, _space.IdOf(_target));
            SnapToTile();
        }

        // One fixed tick: the board moves the unit on.
        private void OnTick(float seconds)
        {
            if (!Placed || Dashing) return;
            SetMoving(Board.Tick(Id));
        }

        // Every frame: the model between its ticks.
        internal void Present()
        {
            if (!Placed || Dashing) return;

            Vector3 position = _transform.position;
            Vector3 facing;
            if (Board.TryGetStep(Id, out HexCoord from, out HexCoord to, out int progress))
            {
                float alpha = _drive != null ? _drive.Clock.Alpha : 0f;
                Vector3 a = TileCentre(from);
                Vector3 b = TileCentre(to);
                _transform.position = Vector3.Lerp(a, b, (progress + alpha) / Board.StepTicks);
                facing = b - a;
            }
            else
            {
                _transform.position = TileCentre(Board.TileOf(Id));
                if (_aim.HasValue) facing = TileCentre(_aim.Value) - position;
                else facing = _target?.View != null && _target.IsAlive ? _target.View.transform.position - position : Vector3.zero;
            }

            facing.y = 0f;
            if (facing.sqrMagnitude < 1e-6f) return;

            float seconds = _timeBundle != null ? _timeBundle.TimeContext.DeltaTime : 0f;
            _transform.rotation = Quaternion.RotateTowards(_transform.rotation, Quaternion.LookRotation(facing), TurnDegreesPerSecond * seconds);
        }

        private void SetMoving(bool moving)
        {
            if (moving == _moving) return;
            _moving = moving;
            _view.AnimationController?.Play(moving ? _view.MoveAnimationKind : AnimationKinds.Idle);
        }

        private void SnapToTile() => _transform.position = TileCentre(Board.TileOf(Id));

        // A tile's centre at the unit's own height.
        private Vector3 TileCentre(HexCoord hex)
        {
            Vector3 centre = Board.Layout.ToWorld(hex);
            centre.y = _transform.position.y;
            return centre;
        }

        private void StopDash()
        {
            if (Dashing) _dash.Stop();
            _dashCancel.Dispose();
        }
    }
}
