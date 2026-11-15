using UnityEngine;

namespace Mightys.Pulse.Samples
{
    // Add this sample component to an empty GameObject. The SDK itself needs no scene component.
    public sealed class BasicPulse : MonoBehaviour
    {
        [SerializeField] private string appKey = "APP_KEY";
        private void Start() { Pulse.Initialize(appKey); }
        private void OnGUI()
        {
            if (GUI.Button(new Rect(20, 20, 220, 45), "Send signal")) Pulse.Track("sample.click");
            if (GUI.Button(new Rect(20, 80, 220, 45), "Send 100"))
                for (var i = 0; i < 100; i++) Pulse.Track("sample.batch", i);
            if (GUI.Button(new Rect(20, 140, 220, 45), "Flush")) _ = Pulse.Flush();
        }
    }
}
