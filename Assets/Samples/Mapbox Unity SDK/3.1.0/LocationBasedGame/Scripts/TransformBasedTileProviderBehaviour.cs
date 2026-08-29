using Mapbox.UnityMapService.TileProviders;

namespace Mapbox.Example.Scripts.TileProviderBehaviours
{
    public class TransformBasedTileProviderBehaviour : TileProviderBehaviour
    {
        public TransformBasedTileProvider TileProvider;
        public override TileProvider Core
        {
            get
            {
                if (TileProvider.Transform == null)
                    TileProvider.Transform = transform;
                return TileProvider;
            }
        }
    }
}