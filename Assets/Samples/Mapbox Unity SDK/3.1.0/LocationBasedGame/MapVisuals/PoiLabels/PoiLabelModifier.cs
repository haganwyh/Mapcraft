using System;
using Mapbox.BaseModule.Data.Tiles;
using Mapbox.BaseModule.Map;
using Mapbox.BaseModule.Unity;
using Mapbox.BaseModule.Utilities;
using TMPro;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    
    [Serializable]
    public class PoiLabelModifier : PrefabModifier
    { 
        public PoiLabelModifier(UnityContext unityContext, PrefabModifierSettings settings) : base(unityContext, settings)
        {
        }

        public override void Run(VectorEntity ve, IMapInformation mapInformation)
        {
            base.Run(ve, mapInformation);
            var label = ve.GameObject.GetComponentInChildren<TextMeshPro>();
            if (label != null)
                label.text = ve.Feature.Properties["name"].ToString();
        }
    }
}