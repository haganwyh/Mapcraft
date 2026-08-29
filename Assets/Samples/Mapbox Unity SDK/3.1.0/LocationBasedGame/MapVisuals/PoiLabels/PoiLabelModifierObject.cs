using System;
using System.ComponentModel;
using Mapbox.BaseModule.Unity;
using Mapbox.VectorModule.MeshGeneration.Unity;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    [DisplayName("Poi Label Modifier")]
    [CreateAssetMenu(menuName = "Mapbox/Modifiers/Poi Label Modifier")]
    public class PoiLabelModifierObject : ScriptableGameObjectModifierObject
    {
        public Action<GameObject> PrefabCreated = (s) => { };

        public PrefabModifierSettings Settings;
        private PoiLabelModifier _prefabModifierImplementation;
        protected override GameObjectModifier _gameObjectModifierImplementation => _prefabModifierImplementation;

        public override void ConstructModifier(UnityContext unityContext)
        {
            _prefabModifierImplementation = new PoiLabelModifier(unityContext, Settings);
            _prefabModifierImplementation.PrefabCreated += PrefabCreated;
        }
    }
}