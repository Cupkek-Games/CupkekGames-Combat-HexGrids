using System.Collections.Generic;
using CupkekGames.HexGrids;
using UnityEngine;

namespace CupkekGames.Combat.HexGrids
{
    /// <summary>
    /// Marks each fighting unit's tile (the one it is stepping to while it walks) with a ring
    /// in its team's colour, so the grid's rules read in play. One mesh, rebuilt every frame
    /// after the units move. Give it a transparent, vertex-coloured material; keep the object
    /// unrotated and unscaled.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class HexCombatTileMarks : MonoBehaviour
    {
        [SerializeField] private HexCombatSpace _space;

        [Tooltip("A ring's colour by team id (0 the allies, 1 the enemies); a team past the list uses the last.")]
        [SerializeField] private Color[] _teamColors = { new Color(0.35f, 0.75f, 1f, 0.9f), new Color(1f, 0.4f, 0.35f, 0.9f) };

        [Tooltip("Where the ring sits, as a share of the way from a tile's centre to its corner.")]
        [SerializeField, Range(0.5f, 1f)] private float _inset = 0.84f;

        [Tooltip("The ring's width in metres.")]
        [SerializeField, Min(0.001f)] private float _width = 0.06f;

        [Tooltip("Metres above the field's plane (above any paint on the field).")]
        [SerializeField] private float _lift = 0.03f;

        private Mesh _mesh;
        private readonly List<HexTileStyle> _tiles = new List<HexTileStyle>();
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Color> _vertexColors = new List<Color>();
        private readonly List<int> _triangles = new List<int>();

        private void Awake()
        {
            if (_space == null)
            {
                throw new System.InvalidOperationException($"[HexCombatTileMarks] '{name}' has no HexCombatSpace to mark.");
            }

            if (_teamColors == null || _teamColors.Length == 0)
            {
                throw new System.InvalidOperationException($"[HexCombatTileMarks] '{name}' has no team colours.");
            }

            _mesh = new Mesh { name = "Hex Combat Tile Marks" };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
        }

        private void LateUpdate()
        {
            _tiles.Clear();
            foreach (HexCombatMover mover in _space.Movers)
            {
                CombatUnit unit = mover.View.CombatUnit;
                if (!mover.Placed || unit == null) continue;
                _tiles.Add(new HexTileStyle(mover.Tile, Color.clear, _teamColors[Mathf.Clamp(unit.TeamId, 0, _teamColors.Length - 1)]));
            }

            HexLayout layout = _space.Board.Layout;
            HexTileMesh.Build(_mesh, layout.WithOrigin(layout.Origin - transform.position), _tiles,
                _inset, _width, 0f, _lift, 1f, _vertices, _vertexColors, _triangles);
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}
