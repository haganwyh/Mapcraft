using Mapbox.UnityMapService.TileProviders;

namespace Mapbox.Example.Scripts.TileProviderBehaviours
{
    public class FixedAreaTileProviderBehaviour : TileProviderBehaviour
    {
        public UnityFixedAreaTileProvider TileProvider;
        public override TileProvider Core => TileProvider;
    }
}