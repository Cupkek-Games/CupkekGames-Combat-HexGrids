using System.Collections.Generic;
using System.Threading;
using CupkekGames.HexGrids;
using CupkekGames.TimeSystem;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace CupkekGames.Combat.HexGrids
{
    /// <summary>
    /// A warning on the hex field: the tiles an area covers, outlined in its colour at once,
    /// their fill rising to the colour over the wind-up. A cast cut short takes it down.
    /// <see cref="HexCombatSpace.ShowArea"/> makes them and reuses hidden ones.
    /// </summary>
    [RequireComponent(typeof(HexTilePainter))]
    public sealed class HexAreaMark : CombatAreaMark
    {
        // The outline reads at least this opaque, however faint the fill's colour.
        private const float RingAlpha = 0.9f;

        // Above the team rings (0.03) and any ground paint.
        private const float Lift = 0.04f;

        private HexTilePainter _painter;
        private readonly List<HexCoord> _tiles = new List<HexCoord>();
        private Color _color;
        private float _fillOpacity;

        /// <summary>The tiles warned of.</summary>
        public IReadOnlyList<HexCoord> Tiles => _tiles;

        /// <summary>How full the fill stands, 0 to 1.</summary>
        public float FillLevel { get; private set; }

        internal static HexAreaMark Create(HexFieldView field, Material material)
        {
            var go = new GameObject("Area Mark");
            go.SetActive(false);
            go.transform.SetParent(field.transform, false);
            go.AddComponent<MeshFilter>();
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            HexTilePainter painter = go.AddComponent<HexTilePainter>();
            painter.Field = field;
            painter.Lift = Lift;

            HexAreaMark mark = go.AddComponent<HexAreaMark>();
            mark._painter = painter;
            return mark;
        }

        internal void Show(List<HexCoord> tiles, Color color, float fillOpacity)
        {
            BeginShowing();
            _tiles.Clear();
            _tiles.AddRange(tiles);
            _color = color;
            _fillOpacity = fillOpacity;
            gameObject.SetActive(true);
            Paint(0f);
        }

        public override void Fill(float seconds, TimeBundle time, CancellationToken cancellationToken)
        {
            if (seconds <= 0f)
            {
                Paint(1f);
                return;
            }

            Rise(seconds, time, cancellationToken, Showing).Forget();
        }

        public override void Hide() => gameObject.SetActive(false);

        private async UniTaskVoid Rise(float seconds, TimeBundle time, CancellationToken cancellationToken, int showing)
        {
            float passed = 0f;
            while (passed < seconds)
            {
                if (showing != Showing || this == null) return;
                if (cancellationToken.IsCancellationRequested)
                {
                    Hide();
                    return;
                }

                Paint(passed / seconds);
                await UniTask.Yield();
                passed += time.TimeContext.DeltaTime;
            }

            if (showing == Showing && this != null) Paint(1f);
        }

        private void Paint(float fill)
        {
            FillLevel = fill;
            Color fillColor = _color;
            fillColor.a = _color.a * _fillOpacity * fill;
            Color ringColor = _color;
            ringColor.a = Mathf.Max(_color.a, RingAlpha);

            _painter.Clear();
            foreach (HexCoord tile in _tiles) _painter.Paint(tile, fillColor, ringColor);
            _painter.Apply();
        }
    }
}
