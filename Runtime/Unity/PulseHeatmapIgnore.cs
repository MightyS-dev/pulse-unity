using UnityEngine;

namespace Mightys.Pulse
{
    /// <summary>Add this to a UI object to keep taps on it (and on its children) out of the heatmaps. The tap is not
    /// recorded at all. Heatmaps are off until the owner switches them on in the Pulse portal.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MightyS Pulse/Heatmap Ignore")]
    public sealed class PulseHeatmapIgnore : MonoBehaviour
    {
    }
}
