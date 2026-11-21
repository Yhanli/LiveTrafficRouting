using Unity.Entities;
using Unity.Mathematics;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Decides which vehicles "have live navigation". Fixed per vehicle, so the same vehicles are always in the
    /// group. Shared by <see cref="RerouteSystem"/> (who may re-route) and <see cref="TrafficReportSystem"/>
    /// (comparing trips with and without navigation). Burst-compatible.
    /// </summary>
    internal static class NavigationGroup
    {
        public static bool Has(Entity vehicle, uint sharePercent)
        {
            return math.hash(new uint2((uint)vehicle.Index, 0x9E3779B9u)) % 100u < sharePercent;
        }

        public static uint SharePercent(float share)
        {
            return (uint)math.clamp((int)math.round(share * 100f), 0, 100);
        }
    }
}
