using System;
using UnityEngine;
using UnityEngine.UI;

namespace BraidsSynth
{
    /// <summary>
    /// Thin wrapper over UI Factory 3 (https://gitlab.com/dagriefaa/ui-factory-3),
    /// Workshop item 2913469777.
    ///
    /// UI Factory ships Besiege's real interface as Unity prefabs -- window frame,
    /// buttons, sliders, font, hover and press animations. Instantiating those is
    /// the only way for a mod panel to look like part of the game: Besiege's own
    /// panel materials are reachable only from inside a custom mapper selector, and
    /// `InternalModding` is blacklisted.
    ///
    /// UI Factory is a **soft** dependency here. The block works from its ordinary
    /// mapper without it; only the panel needs it. That is why every reference to
    /// `Besiege.UI` in this mod is in this one file: a type that cannot be resolved
    /// fails as the method mentioning it is compiled, so keeping the mentions in one
    /// class means one guarded call site can tell whether the panel can exist at all
    /// -- see <see cref="Available"/>.
    /// </summary>
    public static class UIF
    {
        /// <summary>The name UI Factory registers its own prefabs under.</summary>
        public const string Package = "UIFactory3";

        // Prefab names, as registered by UI Factory's Mod.OnAllResourcesLoaded.
        public const string WindowPrefab = "Window";
        public const string TextPrefab = "Text";
        public const string ButtonPrefab = "Text Button";
        public const string SliderPrefab = "Slider";
        public const string PanelPrefab = "Panel";

        /// <summary>
        /// Besiege's red, which is what the game paints the option in force. Kept
        /// here rather than read from <c>Besiege.UI.Consts</c> so that the panel's
        /// colours are not another thing that has to resolve before it can draw.
        /// </summary>
        public static readonly Color Selected = new Color(0.92f, 0.13f, 0.29f, 1f);

        /// <summary>The game's panel black, at the alpha it uses.</summary>
        public static readonly Color PanelBlack = new Color(0.03f, 0.03f, 0.044f, 0.2f);

        /// <summary>The lettering colour for anything that is not the answer.</summary>
        public static readonly Color QuietInk = new Color(0.72f, 0.72f, 0.74f, 1f);

        /// <summary>The cyan Besiege uses for a reset, and here for the trace.</summary>
        public static readonly Color Trace = new Color(0.012f, 1f, 0.847f, 1f);

        private static bool asked;
        private static bool available;

        /// <summary>
        /// Whether UI Factory is installed and has finished loading its bundle.
        ///
        /// This is the guarded call site the whole soft dependency rests on. Touching
        /// <c>Besiege.UI</c> at all is what fails when the mod is absent, and it
        /// fails as the calling method is compiled -- so the try has to be here, one
        /// frame away from any of the panel's own code, and the answer is remembered
        /// rather than asked for again every frame.
        /// </summary>
        public static bool Available
        {
            get
            {
                if (asked)
                {
                    return available;
                }
                try
                {
                    available = Besiege.UI.Make.Instance != null
                             && Modding.ModResource.AllResourcesLoaded;
                }
                catch (Exception)
                {
                    available = false;
                }
                // Only remembered once it is true: UI Factory loads its bundle a
                // moment after the mod does, and "not yet" is not "not installed".
                asked = available;
                return available;
            }
        }

        /// <summary>
        /// Instantiates one of UI Factory's prefabs. Returns null (and says why)
        /// rather than throwing into the caller.
        /// </summary>
        public static GameObject Spawn(string prefab, Transform parent)
        {
            try
            {
                return Besiege.UI.Make.Prefab(Package, prefab, parent);
            }
            catch (Exception e)
            {
                Log.Warn("UI Factory could not supply the prefab '" + prefab + "': " + e.Message);
                return null;
            }
        }

        /// <summary>Besiege's own UI font, as UI Factory resolved it from the game.</summary>
        public static Font Font
        {
            get
            {
                try
                {
                    return Besiege.UI.Make.Font;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Takes the Translator off a label. UI Factory's text carries one, and it
        /// would put the prefab's own wording back at the next language change.
        /// </summary>
        public static void Untranslate(Text label)
        {
            if (label == null)
            {
                return;
            }
            try
            {
                Besiege.UI.Behaviours.Translator translator =
                    label.GetComponent<Besiege.UI.Behaviours.Translator>();
                if (translator != null)
                {
                    UnityEngine.Object.Destroy(translator);
                }
            }
            catch (Exception)
            {
                // A UI Factory without that behaviour still gives usable text.
            }
        }

        /// <summary>
        /// Takes a control's hover swell away. A hovered button grows 15%, which is
        /// right for a button the size of a button and wrong for a full-width row --
        /// it carries the text sideways instead of lighting the row up. Switched off
        /// rather than turned down, the two scales being private to UI Factory: a
        /// disabled behaviour is never told the pointer arrived.
        /// </summary>
        public static void NoSwell(GameObject control)
        {
            if (control == null)
            {
                return;
            }
            try
            {
                Besiege.UI.Bridge.ScaleAnimation scale =
                    control.GetComponent<Besiege.UI.Bridge.ScaleAnimation>();
                if (scale != null)
                {
                    scale.enabled = false;
                }
            }
            catch (Exception)
            {
                // An older UI Factory without that behaviour simply never swelled.
            }
        }

        /// <summary>
        /// Makes any rect a drag handle for <paramref name="target"/>. UI Factory
        /// puts one of these on the window's top bar and nowhere else.
        ///
        /// Target is set in the same breath as the component, because Drag.Start
        /// fills a null one in with its own transform -- which would drag the handle
        /// out of the window rather than moving the window.
        /// </summary>
        public static void Draggable(GameObject handle, RectTransform target)
        {
            if (handle == null || target == null)
            {
                return;
            }
            try
            {
                Besiege.UI.Bridge.Drag drag = handle.AddComponent<Besiege.UI.Bridge.Drag>();
                drag.Target = target;
            }
            catch (Exception)
            {
                // Without it the window simply stays where it is put.
            }
        }
    }
}
