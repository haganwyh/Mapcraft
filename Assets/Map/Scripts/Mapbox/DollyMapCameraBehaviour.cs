using Mapbox.BaseModule.Map;
using UnityEngine;
using UnityEngine.Serialization;

namespace Mapbox.Example.Scripts.MapInput
{
	public class DollyMapCameraBehaviour : MapCameraBehaviour<DollyMapCamera>
	{
		[Tooltip("Slippy map camera settings. Camera stays static while the map moves underneath")]
		[FormerlySerializedAs("Core")]
		[SerializeField] private DollyMapCamera _core;

		public override DollyMapCamera Core => _core;

		protected override void OnMapInitialized(MapboxMap map)
		{
			// Defensive: if MapBehaviour.Initialized fires twice (re-init, swapped map
			// asset), unsubscribe from the previous MapInformation before binding to
			// the new one. Mirrors base.OnMapInitialized, but we can't just call base —
			// it would invoke the 2-arg Core.Initialize and we'd re-init with the
			// 3-arg control-plane overload below.
			if (IsInitialized && Map != null && Map.MapInformation != null)
			{
				Core?.Teardown(Map.MapInformation);
			}

			Map = map;
			IsInitialized = true;
			_core.Initialize(Camera, map.MapInformation, new Plane(MapBehaviour.transform.up, MapBehaviour.transform.position));
		}
	}
}
