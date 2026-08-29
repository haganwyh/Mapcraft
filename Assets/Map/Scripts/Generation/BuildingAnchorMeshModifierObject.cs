using System.ComponentModel;
using Mapbox.BaseModule.Unity;
using Mapbox.VectorModule.MeshGeneration.Unity;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.MeshModifiers.ScriptableObjects
{
    /// <summary>
    /// Asset wrapper for BuildingAnchorMeshModifier. Create one via
    /// Create > Mapbox > Modifiers > Mesh Modifiers > Building Anchor Mesh Modifier,
    /// then add it to your building Modifier Stack's Mesh Modifiers list, AFTER the
    /// Polygon Mesh Modifier and Height Modifier (it needs the finished vertices).
    ///
    /// This writes each building's object-space centroid into UV channel `UvChannel` so the
    /// BuildingShrink shader can shrink whole buildings uniformly — and it works with
    /// "Merge Objects" enabled, because the merge concatenates UV channels per vertex.
    /// </summary>
    [DisplayName("Building Anchor Mesh Modifier")]
    [CreateAssetMenu(menuName = "Mapbox/Modifiers/Mesh Modifiers/Building Anchor Mesh Modifier")]
    public class BuildingAnchorMeshModifierObject : ScriptableMeshModifierObject
    {
        [Tooltip("UV channel index to store the per-building anchor in. 1 = TEXCOORD1 (UV2 in the " +
                 "shader). Must be >= 1 so UV0 (textures) is never overwritten. The shader must " +
                 "read the same channel.")]
        [Min(1)] public int UvChannel = 1;

        private BuildingAnchorMeshModifier _impl;
        protected override MeshModifier _meshModifierImplementation => _impl;

        public override void ConstructModifier(UnityContext unityContext)
        {
            _impl = new BuildingAnchorMeshModifier(UvChannel);
        }
    }
}
