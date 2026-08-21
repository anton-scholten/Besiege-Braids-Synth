using System.Collections.Generic;
using UnityEngine;

namespace BraidsSynth
{
    /// <summary>
    /// Stops clicks aimed at the panel from also reaching the game underneath.
    ///
    /// Besiege's buttons and popups are colliders answering Unity's legacy
    /// <c>OnMouseOver</c>, which is raycast from the cameras and knows nothing about
    /// uGUI's EventSystem. A canvas drawn over one hides it without stopping it, and
    /// raising the canvas does not help -- there is nothing to raise above.
    ///
    /// The lever is <c>Camera.eventMask</c>: a legacy mouse raycast uses
    /// <c>cullingMask &amp; eventMask</c>, so zeroing the event mask makes the game
    /// deaf to the mouse. This holds every camera's mask down while the pointer is
    /// inside the panel and puts each one back the moment it leaves.
    ///
    /// Two things it has to get right: gather the cameras every frame, since one
    /// built while the shield is up would otherwise be the hole in it, and release
    /// from OnDisable as well -- a shield left up is a game whose own buttons have
    /// stopped answering.
    /// </summary>
    public class ClickShield : MonoBehaviour
    {
        private readonly List<Camera> held = new List<Camera>();
        private readonly List<int> masks = new List<int>();
        private RectTransform guarded;
        private bool up;

        /// <summary>The rect the pointer has to be inside for the shield to go up.</summary>
        public void Guard(RectTransform rect)
        {
            guarded = rect;
        }

        private void LateUpdate()
        {
            bool wanted = guarded != null
                       && guarded.gameObject.activeInHierarchy
                       && RectTransformUtility.RectangleContainsScreenPoint(
                              guarded, Input.mousePosition, null);
            if (wanted)
            {
                Raise();
            }
            else
            {
                Lower();
            }
        }

        private void OnDisable()
        {
            Lower();
        }

        private void OnDestroy()
        {
            Lower();
        }

        private void Raise()
        {
            // Rebuilt every frame rather than cached: a camera created while the
            // shield is up would otherwise never be covered.
            Lower();
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null)
                {
                    continue;
                }
                held.Add(camera);
                masks.Add(camera.eventMask);
                camera.eventMask = 0;
            }
            up = true;
        }

        private void Lower()
        {
            if (!up && held.Count == 0)
            {
                return;
            }
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i] != null)
                {
                    held[i].eventMask = masks[i];
                }
            }
            held.Clear();
            masks.Clear();
            up = false;
        }
    }
}
