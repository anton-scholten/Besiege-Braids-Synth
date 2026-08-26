using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace BraidsSynth
{
    // Stops the camera zooming while the pointer is over this, so the wheel
    // scrolls a list rather than pulling the camera in.
    //
    // DisableCameraZoom is a counter, not a flag -- Besiege's own scrollbars hold
    // it the same way -- so every hold must be given back exactly once, including
    // when the object goes away while still hovered. Enter and exit reach the whole
    // chain of parents, so one of these on a window covers every row in it.
    // Taken as-is from Special Effects, by the same author, because Chooser
    // needs it. Keep it in step with that copy rather than letting the two
    // drift.
    public class ZoomGuard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private bool held;

        public void OnPointerEnter(PointerEventData pointer) { Hold(true); }
        public void OnPointerExit(PointerEventData pointer) { Hold(false); }

        private void OnDisable() { Hold(false); }

        private void Hold(bool on)
        {
            if (held == on) return;
            try
            {
                StatMaster.DisableCameraZoom(on);
                held = on;
            }
            catch (Exception)
            {
                held = false;
            }
        }
    }
}
