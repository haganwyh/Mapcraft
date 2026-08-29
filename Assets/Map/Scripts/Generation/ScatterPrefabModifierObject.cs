using System;
using System.ComponentModel;
using Mapbox.BaseModule.Unity;
using Mapbox.VectorModule.MeshGeneration.Unity;
using UnityEngine;

namespace Mapbox.VectorModule.MeshGeneration.GameObjectModifiers
{
    [DisplayName("Scatter Prefab Modifier")]
    [CreateAssetMenu(menuName = "Mapbox/Modifiers/GameObject Modifiers/Scatter Prefab Modifier")]
    public class ScatterPrefabModifierObject : ScriptableGameObjectModifierObject
    {
        public Action<GameObject> PrefabCreated = (s) => { };

        public ScatterPrefabModifierSettings Settings;
        private ScatterPrefabModifier _impl;
        protected override GameObjectModifier _gameObjectModifierImplementation => _impl;

        public override void ConstructModifier(UnityContext unityContext)
        {
            _impl = new ScatterPrefabModifier(unityContext, Settings);
            _impl.PrefabCreated += PrefabCreated;
        }
    }
}