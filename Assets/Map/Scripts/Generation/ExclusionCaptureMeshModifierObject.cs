using System.ComponentModel;
using Mapbox.BaseModule.Unity;
using Mapbox.VectorModule.MeshGeneration.GameObjectModifiers; // ExclusionGeometryType
using Mapbox.VectorModule.MeshGeneration.Unity;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.MeshModifiers.ScriptableObjects
{
    [DisplayName("Exclusion Capture Mesh Modifier")]
    [CreateAssetMenu(menuName = "Mapbox/Modifiers/Mesh Modifiers/Exclusion Capture Mesh Modifier")]
    public class ExclusionCaptureMeshModifierObject : ScriptableMeshModifierObject
    {
        [Tooltip("Polygon for water/buildings (reject points inside). Line for roads (reject points near the line).")]
        public ExclusionGeometryType GeometryType = ExclusionGeometryType.Polygon;

        [Tooltip("Line simplification strength for roads, in normalized tile units (Line type only). " +
                 "Tile span is ~1 unit, so use small values, e.g. 0.002–0.01. 0 = no simplification.")]
        public float SimplifyEpsilon = 0.004f;

        [Header("Tile Zoom Gate")]
        [Tooltip("Leave FULLY OPEN (0) unless you have a measured reason not to. The module's " +
                 "'Reject Tiles Outside Zoom' already bounds which tiles generate; a capture " +
                 "gate tighter than the tiles the module actually loads produces tiles with NO " +
                 "exclusion data — which has caused scatter-on-water bugs twice in this project.")]
        public int MinTileZoom = 0;

        [Tooltip("Upper bound of the capture zoom range. Leave at 22.")]
        public int MaxTileZoom = 22;

        [Header("Invisible Features")]
        // NAMING IS DELIBERATE: these are Include* with FALSE as the desired default, because
        // Unity fills fields that are missing from an EXISTING asset's serialized data with
        // the TYPE DEFAULT — for bool, false. Had these been Skip* = true, every asset already
        // on disk would silently deserialize them as false and keep capturing tunnels. This
        // way, stale assets and fresh assets behave identically and safely. (Same trap that
        // bit MinTileZoom on this very asset type — see the project reference notes.)
        [Tooltip("Capture road features with structure=tunnel. Tunnels render nothing at " +
                 "ground level, so capturing them creates invisible no-spawn stripes.")]
        public bool IncludeTunnels = false;

        [Tooltip("Capture road features with class=ferry. Ferry routes are lines across open " +
                 "water; capturing them creates invisible no-spawn lanes.")]
        public bool IncludeFerryRoutes = false;

        [Tooltip("Capture building-layer features with underground=true (metro stations, " +
                 "basements, underpasses). These render nothing at ground level, so capturing " +
                 "them creates invisible no-spawn blobs.")]
        public bool IncludeUndergroundBuildings = false;

        private ExclusionCaptureMeshModifier _impl;
        protected override MeshModifier _meshModifierImplementation => _impl;

        public override void ConstructModifier(UnityContext unityContext)
        {
            _impl = new ExclusionCaptureMeshModifier(GeometryType, SimplifyEpsilon,
                MinTileZoom, MaxTileZoom,
                IncludeTunnels, IncludeFerryRoutes, IncludeUndergroundBuildings);
        }
    }
}