using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BraidsSynth
{
    /// <summary>
    /// The synth block's own panel, built out of UI Factory's prefabs so it looks
    /// like part of Besiege rather than like a mod.
    ///
    /// It exists because the block mapper cannot say what this block does. A
    /// macro-oscillator is twenty-three models whose two controls mean something
    /// different under each of them, and a stack of sliders called TIMBRE and COLOR
    /// tells you none of that. The panel names the models, says what the controls do
    /// in the one that is chosen, draws the wave coming out, and will play it while
    /// the machine is still being built -- so a model can be chosen by ear.
    ///
    /// UI Factory is a soft dependency. Everything here goes through
    /// <see cref="UIF"/>, and if it is not installed the panel never appears; the
    /// block keeps its ordinary mapper, which is what it saves through either way.
    ///
    /// The panel opens with the block mapper and closes with it, which is Besiege's
    /// own idea of when a block's settings are being looked at.
    /// </summary>
    public class BraidsPanel : MonoBehaviour
    {
        // Sits below 30000, which is what UnityEngine.UI.Dropdown hardcodes for a
        // popup list -- a canvas that ties with it leaves the list unclickable.
        private const int CanvasOrder = 2400;

        /// <summary>
        /// Taken from Besiege's mapper every frame, so the two read as one window.
        /// The starting value only matters until the first dock.
        /// </summary>
        private float width = 520f;
        private const float Margin = 12f;
        private const float ScopeHeight = 96f;
        private const float RowHeight = 26f;
        private const float RowGap = 3f;
        /// <summary>How wide the model chooser's name is, between its two arrows.</summary>
        private const float SelectorWidth = 250f;

        /// <summary>How often the trace is redrawn. Fast enough to look live.</summary>
        private const float ScopeInterval = 0.05f;

        /// <summary>
        /// How much screen is left under the panel, so it stops short of the bottom
        /// edge rather than running into it.
        /// </summary>
        private const float Clearance = 56f;

        private static readonly Vector2 Reference = new Vector2(1920f, 1080f);

        private BraidsBehaviour block;
        private bool hooked;
        private bool built;
        private bool failed;

        private GameObject window;
        private RectTransform windowRect;

        /// <summary>Where the rows go: the Window prefab's own scroll content.</summary>
        private Transform host;
        private RectTransform content;

        /// <summary>How tall the rows came out, before the frame caps them.</summary>
        private float contentHeight;

        /// <summary>The camera that draws the mapper. See MapperCamera.</summary>
        private Camera mapperEye;
        private ClickShield shield;

        private RawImage scopeImage;
        private Scope scope;
        private float[] samples;
        private float nextScope;

        private Chooser modelPicker;

        /// <summary>The selector's list, built once; it never changes.</summary>
        private readonly List<string> modelNames = BraidsModels.MenuItems();
        private int shownModel = -1;

        private Text timbreMeaning;
        private Text colourMeaning;
        private Text previewLabel;
        private Image previewMark;

        // What a dial's number means, which is what a typed one has to be read as.
        // const int rather than an enum: Besiege's compiler segfaults on those.
        private const int KindNumber = 0;
        private const int KindPercent = 1;
        private const int KindSeconds = 2;
        private const int KindNote = 3;

        private Dial[] dials;
        private bool typing;

        private Dial note;
        private Dial fine;
        private Dial timbre;
        private Dial colour;
        private Dial volume;
        private Dial attack;
        private Dial release;
        private Dial range;

        /// <summary>
        /// A row of the panel: a name, one of UI Factory's sliders, and the value
        /// written out. Bound to one of the block's mapper sliders, which is what the
        /// machine saves -- the panel never keeps a value of its own.
        /// </summary>
        private class Dial
        {
            public UnityEngine.UI.Slider Control;
            public UnityEngine.UI.InputField Field;
            public Text Value;
            public Text Name;
            public MSlider Bound;
            public bool Writing;

            /// <summary>True from the click into the field until focus leaves it.</summary>
            public bool Editing;

            /// <summary>Set for one frame, so the click that focused it lands first.</summary>
            public bool SelectPending;

            /// <summary>
            /// Set where the slider spans less than the setting allows. Dragging
            /// stays over the useful range; typing reaches the setting's own limit.
            /// </summary>
            public bool Narrowed;
            public float DragMin;
            public float DragMax;

            /// <summary>How the value is written and read back. One of Kind*.</summary>
            public int Kind;

            /// <summary>
            /// What the dial rounds to, or zero for a control with no natural step.
            /// A note is the case that matters: dragged freely it lands a quarter of
            /// a semitone sharp and the block is unplayable in a tune, and the
            /// in-between pitches are what FINE is for.
            /// </summary>
            public float Step;
        }

        /// <summary>
        /// Settings changed on the panel that Besiege has not been told about yet.
        ///
        /// A mapper setting is stored twice: the live value, and the value the block
        /// is *loaded* from. Assigning <c>MapperType.Value</c> writes only the first,
        /// which is why a panel that did just that was heard by the preview -- which
        /// reads the live value -- and ignored by a simulation, which is built from
        /// the other one.
        ///
        /// Committing is what reconciles them, and it is not free: Besiege's own
        /// path reserialises the block and adds an undo entry. So a drag writes the
        /// live value every frame, so the preview follows the knob, and commits once
        /// when the drag ends.
        /// </summary>
        private readonly List<MapperType> pending = new List<MapperType>();

        // ---- lifetime ----------------------------------------------------------

        private void Start()
        {
            Hook();
        }

        private void OnDestroy()
        {
            // Besiege counts this, so a panel torn down mid-edit would leave the game
            // believing a menu were still open.
            HoldInput(false);
            Unhook();
            if (scope != null)
            {
                scope.Dispose();
            }
        }

        private void Hook()
        {
            if (hooked)
            {
                return;
            }
            try
            {
                BlockMapper.onMapperOpen += OnMapperOpen;
                BlockMapper.onMapperClose += OnMapperClose;
                hooked = true;
            }
            catch (Exception e)
            {
                Log.Warn("could not watch the block mapper, so the panel will not open: "
                         + e.Message);
            }
        }

        private void Unhook()
        {
            if (!hooked)
            {
                return;
            }
            try
            {
                BlockMapper.onMapperOpen -= OnMapperOpen;
                BlockMapper.onMapperClose -= OnMapperClose;
            }
            catch (Exception)
            {
                // Nothing useful to do while the game is being torn down.
            }
            hooked = false;
        }

        private void OnMapperOpen()
        {
            BraidsBehaviour opened = null;
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper != null && mapper.Block != null)
                {
                    opened = mapper.Block.GetComponent<BraidsBehaviour>();
                }
            }
            catch (Exception e)
            {
                Log.Warn("could not tell which block the mapper opened on: " + e.Message);
            }

            if (opened == null || opened.IsSimulating)
            {
                Hide();
                return;
            }
            Show(opened);
        }

        private void OnMapperClose()
        {
            Hide();
        }

        private void Show(BraidsBehaviour on)
        {
            block = on;
            if (!Build())
            {
                return;
            }
            Bind();
            window.SetActive(true);
            shownModel = -1;
            ReadFromBlock();
        }

        /// <summary>
        /// Points the dials at this block's sliders. The window is built once and
        /// reused for whichever synth block the mapper opens on next, so what it is
        /// bound to has to be set every time it is shown -- otherwise every synth
        /// block on the machine drives the first one.
        /// </summary>
        private void Bind()
        {
            Point(note, block.Note);
            Point(fine, block.Fine);
            Point(timbre, block.TimbreSlider);
            Point(colour, block.ColourSlider);
            Point(volume, block.Volume);
            Point(attack, block.Attack);
            Point(release, block.Release);
            Point(range, block.Range);
        }

        /// <summary>
        /// Null-tolerant on the dial as well as the slider: a build that lost one
        /// prefab should be a panel missing a row, not a panel that throws.
        /// </summary>
        private static void Point(Dial dial, MSlider at)
        {
            if (dial != null)
            {
                dial.Bound = at;
            }
        }

        private void Hide()
        {
            // Anything mid-drag when the mapper closed still has to reach the block.
            if (block != null && pending.Count > 0)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    Commit(pending[i]);
                }
            }
            pending.Clear();

            for (int i = 0; dials != null && i < dials.Length; i++)
            {
                if (dials[i] != null)
                {
                    dials[i].Editing = false;
                    dials[i].SelectPending = false;
                }
            }
            // The open list hangs off the canvas, not the window, so hiding the
            // window alone would leave it on screen.
            if (modelPicker != null)
            {
                modelPicker.Close();
            }
            HoldInput(false);

            if (block != null)
            {
                block.SetPreview(false);
            }
            block = null;
            if (window != null)
            {
                window.SetActive(false);
            }
        }

        // ---- building ----------------------------------------------------------

        /// <summary>
        /// Builds the window once, or says it cannot. The guard is what makes UI
        /// Factory a soft dependency: <see cref="UIF.Available"/> is the only place
        /// that touches its types before this point, so a missing assembly is one
        /// log line rather than an exception thrown into the mapper's callback.
        /// </summary>
        /// <summary>
        /// Drops the built window and everything that pointed into it. Called before
        /// a rebuild and when the panel goes away for good.
        /// </summary>
        private void Teardown()
        {
            HoldInput(false);
            host = null;
            content = null;
            scopeImage = null;
            dials = null;
            note = null; fine = null; timbre = null; colour = null;
            volume = null; attack = null; release = null; range = null;
            timbreMeaning = null;
            colourMeaning = null;
            previewLabel = null;
            previewMark = null;
            modelPicker = null;
            shownModel = -1;
            if (scope != null)
            {
                scope.Dispose();
                scope = null;
            }
            if (window != null)
            {
                Destroy(window);
                window = null;
                windowRect = null;
            }
            built = false;
        }

        private bool Build()
        {
            if (built)
            {
                return true;
            }
            if (failed)
            {
                return false;
            }
            if (!UIF.Available)
            {
                Log.Info("UI Factory 3 is not available, so the synth block uses Besiege's "
                         + "own mapper. Subscribe to Workshop item 2913469777 for the panel.");
                failed = true;
                return false;
            }

            try
            {
                // Every rebuild starts from nothing. Without this the width change
                // that triggers one spawns a second Window prefab and leaves the
                // first parented to the canvas, active, docked to nothing -- a
                // stray panel loose on the screen.
                Teardown();
                BuildWindow();
                built = true;
            }
            catch (Exception e)
            {
                Log.Warn("could not build the panel (" + e.Message
                         + "); the block's mapper still works.");
                failed = true;
                if (window != null)
                {
                    Destroy(window);
                    window = null;
                }
            }
            return built;
        }

        private void BuildWindow()
        {
            Canvas canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null)
            {
                canvas = gameObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = CanvasOrder;
                canvas.pixelPerfect = false;

                CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                // UI Factory authors its prefabs against 1920x1080 and matches on
                // height; anything else draws Besiege's own widgets at the wrong
                // size beside the game's.
                scaler.referenceResolution = Reference;
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 1f;

                gameObject.AddComponent<GraphicRaycaster>();
            }

            window = UIF.Spawn(UIF.WindowPrefab, canvas.transform);
            if (window == null)
            {
                throw new Exception("UI Factory gave no Window prefab");
            }
            window.name = "Braids panel";

            windowRect = window.transform as RectTransform;
            // Anchored and pivoted by us rather than however the prefab was authored,
            // so the placement below means one thing.
            // Centred anchors and pivot, so the placement in Dock means one thing:
            // anchoredPosition is the window's middle against the screen's.
            windowRect.anchorMin = new Vector2(0.5f, 0.5f);
            windowRect.anchorMax = new Vector2(0.5f, 0.5f);
            windowRect.pivot = new Vector2(0.5f, 0.5f);

            // No title bar: this is the mapper's lower half, not a window of its
            // own. The bar carries a drag handle, which would pull half a window
            // away from the other half, and a close cross that would shut only the
            // half it is on -- and, left in place, the prefab's authored title,
            // which reads exactly like a caption that failed to load.
            RectTransform bar = window.transform.FindChild("TopBar") as RectTransform;
            if (bar != null)
            {
                bar.gameObject.SetActive(false);
            }

            // The prefab anchors its scroll view below that bar, so hiding the bar
            // alone leaves a bar's worth of empty frame above the first row.
            ScrollRect scroll = window.GetComponentInChildren<ScrollRect>(true);
            RectTransform view = scroll == null ? null : scroll.transform as RectTransform;
            if (view != null)
            {
                view.anchorMin = Vector2.zero;
                view.anchorMax = Vector2.one;
                view.offsetMin = Vector2.zero;
                view.offsetMax = Vector2.zero;
            }

            shield = gameObject.GetComponent<ClickShield>();
            if (shield == null)
            {
                shield = gameObject.AddComponent<ClickShield>();
            }
            shield.Guard(windowRect);

            // Rows belong in the scroll view the Window prefab ships with. Put on
            // the window instead, that scroll view keeps the prefab's own 500-unit
            // placeholder -- taller than any panel, so a scrollbar sits there
            // permanently beside an empty region. Filled properly, Besiege's scroll
            // view hides its bars whenever the contents fit.
            content = scroll == null ? null : scroll.content;
            host = content != null ? (Transform)content : window.transform;

            float y = Margin;
            y = BuildScope(y);
            // LISTEN sits under the trace, since what it does is make the trace move.
            y = BuildPreview(y);
            y = BuildModels(y);
            y = BuildMeanings(y);
            y = BuildDials(y);

            contentHeight = y;
            if (content != null)
            {
                content.sizeDelta = new Vector2(content.sizeDelta.x, y);
            }
            // A height is settled at dock time, against the room below the mapper;
            // anything taller than that room scrolls.
            windowRect.sizeDelta = new Vector2(width, y);

            window.SetActive(false);
        }

        // ---- where the window sits ---------------------------------------------

        /// <summary>
        /// Docking runs here rather than in Update: the mapper is dragged by its own
        /// behaviour, and a panel placed before it has moved is one frame behind it,
        /// which reads as the join coming apart while it is dragged.
        /// </summary>
        private void LateUpdate()
        {
            if (block != null && built && window != null && window.activeSelf)
            {
                Dock();
            }
        }

        /// <summary>
        /// Puts the panel against the bottom edge of Besiege's mapper, the same width
        /// as it, so the two read as one window with a seam.
        ///
        /// The mapper is mesh UI in world space -- nothing this could be parented
        /// into -- so the join is made by measuring where it lands on screen, every
        /// frame, because it can be dragged.
        /// </summary>
        private void Dock()
        {
            Rect frame;
            if (!MapperFrame(out frame))
            {
                // Stay where we are rather than jumping to a corner.
                return;
            }

            if (Widen(frame))
            {
                // Rebuilt *and* placed in the same frame. Returning here instead
                // leaves the window where it was and clears the flag that lets this
                // run at all, so it never docks and never follows again.
                if (!Build())
                {
                    return;
                }
                // BuildWindow leaves what it builds switched off, and the Show that
                // would switch it on has already run: the first open takes its width
                // from the mapper, rebuilds here, and without this the panel is built
                // and never seen.
                window.SetActive(true);
                Bind();
                ReadFromBlock();
                Canvas.ForceUpdateCanvases();
            }

            float scale = Scale();
            // As tall as the rows, or as tall as the room under the mapper, whichever
            // is less -- the scroll view takes up the difference.
            float room = frame.yMin * scale - Clearance;
            float tall = Mathf.Max(RowHeight * 4f, Mathf.Min(contentHeight, room));
            windowRect.sizeDelta = new Vector2(width, tall);

            float left = (frame.xMin - Screen.width * 0.5f) * scale;
            float bottom = (frame.yMin - Screen.height * 0.5f) * scale;
            windowRect.anchoredPosition =
                new Vector2(left + width * 0.5f, bottom - tall * 0.5f);
        }

        /// <summary>
        /// Canvas units per screen pixel. The scaler matches on height against a
        /// 1080-tall reference, so one unit is one pixel at 1080p.
        /// </summary>
        private float Scale()
        {
            return Screen.height > 0 ? Reference.y / Screen.height : 1f;
        }

        /// <summary>
        /// Takes the panel's width from the mapper's. True if it changed, in which
        /// case the rows -- laid out to it -- no longer fit and have to be rebuilt.
        /// </summary>
        private bool Widen(Rect frame)
        {
            float wide = frame.width * Scale();
            if (Mathf.Abs(wide - width) <= 0.5f)
            {
                return false;
            }
            width = wide;
            built = false;
            return true;
        }

        /// <summary>
        /// Besiege's mapper window in screen pixels, or false if it cannot be found.
        ///
        /// The window has to be picked out of everything the mapper draws, and the
        /// answer is not guessable -- it was read out of the game. With a block open
        /// at 4K the mapper draws, among other things:
        ///
        ///     Background   874.80 x 389.88   at y 1540.87   &lt;- the window
        ///     Background   874.80 x 281.88   at y 1540.87   &lt;- a section inside it
        ///     WideShadow   972.00 x 194.40   at y 1638.37   &lt;- the shadow, 11% wider
        ///     Visual        93.31 x  93.31                  &lt;- a button
        ///
        /// So the window is a `Background`, they all share its width, and the tallest
        /// of them is the frame. Docking to the widest thing drawn lands on
        /// `WideShadow` and makes the panel an eleventh too wide; docking to `Visual`
        /// by its promising name lands on a 93-pixel button.
        /// </summary>
        private bool MapperFrame(out Rect frame)
        {
            frame = new Rect();
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper == null)
                {
                    return false;
                }
                Renderer[] parts = mapper.GetComponentsInChildren<Renderer>(false);
                Camera eye = null;
                Rect best = new Rect();
                bool found = false;

                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == null || !parts[i].enabled
                        || parts[i].name != "Background")
                    {
                        continue;
                    }
                    if (eye == null)
                    {
                        eye = MapperCamera(parts[i].gameObject.layer);
                        if (eye == null)
                        {
                            Explain("no camera draws layer "
                                    + parts[i].gameObject.layer.ToString());
                            return false;
                        }
                    }
                    Rect here = ScreenRect(parts[i], eye);
                    if (here.width < 1f || here.height < 1f)
                    {
                        continue;
                    }
                    if (!found || here.height > best.height)
                    {
                        found = true;
                        best = here;
                    }
                }

                if (!found)
                {
                    Explain("none of the mapper's " + parts.Length.ToString()
                            + " parts look like its window");
                    return false;
                }
                Explain("docking to the mapper at " + best.ToString());
                frame = best;
                return true;
            }
            catch (Exception e)
            {
                Explain("could not measure the mapper: " + e.Message);
                return false;
            }
        }

        /// <summary>One renderer's world box, in screen pixels.</summary>
        private static Rect ScreenRect(Renderer part, Camera eye)
        {
            Bounds box = part.bounds;
            Vector3 a = eye.WorldToScreenPoint(
                new Vector3(box.min.x, box.min.y, box.center.z));
            Vector3 b = eye.WorldToScreenPoint(
                new Vector3(box.max.x, box.max.y, box.center.z));
            return new Rect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                            Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        /// <summary>
        /// The camera the mapper is drawn by, found by the layer it is on rather than
        /// by name: its interface is in the world, and only the camera rendering that
        /// layer knows where on screen it ends up. The topmost such camera, since
        /// Besiege draws its interface last.
        /// </summary>
        private Camera MapperCamera(int layer)
        {
            if (mapperEye != null && mapperEye.isActiveAndEnabled
                && (mapperEye.cullingMask & (1 << layer)) != 0)
            {
                return mapperEye;
            }
            mapperEye = null;
            Camera[] all = Camera.allCameras;
            for (int i = 0; i < all.Length; i++)
            {
                if ((all[i].cullingMask & (1 << layer)) != 0
                    && (mapperEye == null || all[i].depth > mapperEye.depth))
                {
                    mapperEye = all[i];
                }
            }
            return mapperEye;
        }

        /// <summary>
        /// What the panel found to dock to, said once a session. The join is measured
        /// off another mod's furniture, so when it is wrong this line is the
        /// difference between a diagnosis and a guess.
        /// </summary>
        private static bool explained;

        private static void Explain(string what)
        {
            if (explained)
            {
                return;
            }
            explained = true;
            Log.Info(what);
        }

        /// <summary>Places a rect against the window's top-left corner.</summary>
        private RectTransform Place(GameObject go, float x, float y, float w, float h)
        {
            RectTransform rect = go.transform as RectTransform;
            if (rect == null)
            {
                return null;
            }
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(w, h);
            rect.anchoredPosition = new Vector2(x, -y);
            return rect;
        }

        private Text Label(string text, float x, float y, float w, float h,
                           int size, TextAnchor align, Color ink)
        {
            GameObject go = UIF.Spawn(UIF.TextPrefab, host);
            if (go == null)
            {
                return null;
            }
            Place(go, x, y, w, h);
            Text label = go.GetComponent<Text>();
            if (label == null)
            {
                label = go.GetComponentInChildren<Text>(true);
            }
            if (label == null)
            {
                return null;
            }
            UIF.Untranslate(label);
            label.text = text;
            label.fontSize = size;
            label.resizeTextForBestFit = false;
            label.alignment = align;
            label.color = ink;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        private float BuildScope(float y)
        {
            GameObject go = new GameObject("Scope", typeof(RectTransform));
            go.transform.SetParent(host, false);
            Place(go, Margin, y, width - Margin * 2f, ScopeHeight);

            scopeImage = go.AddComponent<RawImage>();
            scope = new Scope(Mathf.RoundToInt(width - Margin * 2f),
                              Mathf.RoundToInt(ScopeHeight));
            scopeImage.texture = scope.Texture;
            scopeImage.raycastTarget = true;
            samples = new float[BraidsBehaviour.ScopeSize];

            // The trace is a big quiet area, which makes it the natural second place
            // to pick the window up by.
            UIF.Draggable(go, windowRect);

            return y + ScopeHeight + Margin;
        }

        /// <summary>
        /// The model: `&lt; name &gt;`, centred in its row with no caption beside it.
        /// The arrows step to the next model and the name between them opens the
        /// whole list. Special Effects' own control, and built rather than taken from
        /// a prefab for the reason written at the top of it -- the prefab drop-down's
        /// list spills past the panel and paints its overflow over the world.
        ///
        /// The list hangs off the canvas rather than off this row, or the scroll view
        /// would clip it the moment it reached past the bottom of the panel.
        /// </summary>
        private float BuildModels(float y)
        {
            float full = width - Margin * 2f;
            float w = Mathf.Min(SelectorWidth + (Chooser.ArrowWidth + Chooser.ArrowGap) * 2f,
                                full);
            modelPicker = Chooser.Make(host, transform, Margin + (full - w) * 0.5f, y,
                                       w, RowHeight, modelNames,
                                       block != null && block.Model != null
                                           ? block.Model.Value : 0);
            return y + RowHeight + Margin;
        }


        private const int MeaningFontSize = 15;

        private float BuildMeanings(float y)
        {
            // A larger face on the same 18-unit pitch as before: the two lines are
            // read once each time the model changes, and the panel has no room to
            // spend on giving them more height. The labels overflow vertically, so
            // the box stays 16 and the glyphs draw past it.
            timbreMeaning = Label("", Margin, y, width - Margin * 2f, 16f,
                                  MeaningFontSize, TextAnchor.MiddleLeft, UIF.QuietInk);
            y += 18f;
            colourMeaning = Label("", Margin, y, width - Margin * 2f, 16f,
                                  MeaningFontSize, TextAnchor.MiddleLeft, UIF.QuietInk);
            return y + 18f + Margin;
        }

        private float BuildDials(float y)
        {
            note = BuildDial("NOTE", y);
            note.Step = 1f;
            note.Kind = KindNote;
            y += RowHeight + RowGap;
            fine = BuildDial("FINE", y);
            fine.Step = 1f;
            y += RowHeight + RowGap;
            timbre = BuildDial("TIMBRE", y);
            timbre.Kind = KindPercent;
            y += RowHeight + RowGap;
            colour = BuildDial("COLOR", y);
            colour.Kind = KindPercent;
            y += RowHeight + RowGap;
            volume = BuildDial("VOLUME", y);
            volume.Kind = KindPercent;
            y += RowHeight + RowGap;
            attack = BuildDial("ATTACK", y);
            attack.Kind = KindSeconds;
            Narrow(attack, 0f, 2f);
            y += RowHeight + RowGap;
            release = BuildDial("RELEASE", y);
            release.Kind = KindSeconds;
            Narrow(release, 0f, 4f);
            y += RowHeight + RowGap;
            range = BuildDial("RANGE", y);
            range.Step = 1f;
            Narrow(range, 1f, 100f);

            dials = new Dial[] { note, fine, timbre, colour, volume,
                                 attack, release, range };
            return y + RowHeight + Margin;
        }

        /// <summary>
        /// Holds the slider to the range worth dragging over while the setting keeps
        /// its own wider one, which is what a typed value is measured against.
        /// </summary>
        private static void Narrow(Dial dial, float low, float high)
        {
            dial.Narrowed = true;
            dial.DragMin = low;
            dial.DragMax = high;
        }

        private static float DragLow(Dial dial)
        {
            return dial.Narrowed ? dial.DragMin : dial.Bound.Min;
        }

        private static float DragHigh(Dial dial)
        {
            return dial.Narrowed ? dial.DragMax : dial.Bound.Max;
        }

        private const float DialNameWidth = 74f;
        private const float DialValueWidth = 85f;
        // The value slots are typed into, so they carry a larger face than the
        // captions beside them -- a caret is easier to place in a bigger glyph.
        private const int ValueFontSize = 16;

        private Dial BuildDial(string name, float y)
        {
            Dial dial = new Dial();
            dial.Name = Label(name, Margin, y, DialNameWidth, RowHeight, 12,
                              TextAnchor.MiddleLeft, Color.white);

            float left = Margin + DialNameWidth;
            float right = width - Margin - DialValueWidth;

            GameObject go = UIF.Spawn(UIF.SliderPrefab, host);
            if (go != null)
            {
                go.name = name;
                Place(go, left, y, right - left - RowGap, RowHeight);
                // Fully qualified: Besiege has a Slider of its own in the global
                // namespace, and it is the one an unqualified name binds to.
                dial.Control = go.GetComponentInChildren<UnityEngine.UI.Slider>(true);
                if (dial.Control != null)
                {
                    dial.Control.wholeNumbers = false;
                    dial.Control.minValue = 0f;
                    dial.Control.maxValue = 1f;
                    Dial captured = dial;
                    dial.Control.onValueChanged.AddListener(
                        delegate(float v) { OnDialMoved(captured, v); });
                }
            }

            BuildValue(dial, name, right, y, DialValueWidth, RowHeight);
            return dial;
        }

        /// <summary>
        /// The value slot: a label that can be clicked into and typed over.
        ///
        /// UI Factory has no input prefab, so this is its Text with an InputField
        /// built round it. The Text has to be a child rather than the same object,
        /// because an InputField moves a caret about inside itself -- and it drives
        /// that Text from then on, which is why everything else writes the value
        /// through the field rather than to the label.
        /// </summary>
        private void BuildValue(Dial dial, string name, float x, float y,
                                float w, float h)
        {
            Text text = Label("", x, y, w, h, ValueFontSize, TextAnchor.MiddleRight,
                              Color.white);
            if (text == null)
            {
                return;
            }
            dial.Value = text;

            GameObject root = new GameObject(name + " Value", typeof(RectTransform));
            root.transform.SetParent(host, false);
            Place(root, x, y, w, h);

            Image face = root.AddComponent<Image>();
            face.color = UIF.PanelBlack;

            RectTransform inner = text.rectTransform;
            inner.SetParent(root.transform, false);
            inner.anchorMin = Vector2.zero;
            inner.anchorMax = Vector2.one;
            inner.offsetMin = new Vector2(4f, 0f);
            inner.offsetMax = new Vector2(-4f, 0f);
            // An InputField will not have rich text in the box it edits.
            text.supportRichText = false;

            UnityEngine.UI.InputField field =
                root.AddComponent<UnityEngine.UI.InputField>();
            field.textComponent = text;
            field.targetGraphic = face;
            field.lineType = UnityEngine.UI.InputField.LineType.SingleLine;
            field.characterLimit = 12;
            Dial captured = dial;
            field.onEndEdit.AddListener(delegate(string s) { Typed(captured, s); });
            dial.Field = field;
        }

        private float BuildPreview(float y)
        {
            GameObject button = UIF.Spawn(UIF.ButtonPrefab, host);
            if (button == null)
            {
                return y;
            }
            button.name = "Preview";
            Place(button, Margin, y, width - Margin * 2f, PreviewHeight);
            UIF.NoSwell(button);

            Image face = button.GetComponent<Image>();
            GameObject markObject = new GameObject("Mark", typeof(RectTransform));
            markObject.transform.SetParent(button.transform, false);
            RectTransform markRect = markObject.transform as RectTransform;
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = Vector2.zero;
            markRect.offsetMax = Vector2.zero;
            markRect.SetAsFirstSibling();
            previewMark = markObject.AddComponent<Image>();
            if (face != null)
            {
                previewMark.sprite = face.sprite;
                previewMark.type = face.type;
            }
            previewMark.color = new Color(0f, 0f, 0f, 0f);
            previewMark.raycastTarget = false;

            previewLabel = button.GetComponentInChildren<Text>(true);
            if (previewLabel != null)
            {
                UIF.Untranslate(previewLabel);
                previewLabel.fontSize = 12;
                previewLabel.resizeTextForBestFit = false;
                previewLabel.alignment = TextAnchor.MiddleCenter;
                previewLabel.horizontalOverflow = HorizontalWrapMode.Overflow;

                // The prefab's own swell grows the whole plate, which on a
                // full-width row carries the lettering out past the window; its
                // ScaleAnimation went off above. This grows the words instead, and
                // stands down on its own when the button is not interactable.
                Swell swell = button.AddComponent<Swell>();
                swell.grows = previewLabel.transform;
                swell.grown = 1.15f;
            }

            Button click = button.GetComponent<Button>();
            if (click != null)
            {
                click.onClick.AddListener(TogglePreview);
            }
            return y + PreviewHeight + Margin;
        }

        /// <summary>A shade taller than a row, since it is the panel's one action.</summary>
        private const float PreviewHeight = RowHeight + 2f;

        // ---- driving it --------------------------------------------------------

        private void ChooseModel(int model)
        {
            if (block == null || block.Model == null)
            {
                return;
            }
            block.Model.Value = model;
            // A click, not a drag: there is nothing to wait for.
            Commit(block.Model);
            Refresh();
            ShowModel(model);
        }

        private void TogglePreview()
        {
            if (block == null)
            {
                return;
            }
            block.SetPreview(!block.IsPreviewing);
            ShowPreview();
        }

        /// <summary>
        /// Watches which value field has the keyboard, prefills it the moment it is
        /// clicked into, and holds Besiege's own input off while it does.
        /// </summary>
        private void Typing()
        {
            bool any = false;
            for (int i = 0; dials != null && i < dials.Length; i++)
            {
                Dial dial = dials[i];
                if (dial == null || dial.Field == null)
                {
                    continue;
                }
                bool focused = dial.Field.isFocused;
                if (focused && !dial.Editing)
                {
                    dial.Editing = true;
                    // The unit is dropped on the way in for a note, whose written
                    // form is a name *and* a number and so cannot be typed back.
                    if (dial.Bound != null)
                    {
                        dial.Field.text = Editable(dial, dial.Bound.Value);
                    }
                    // Held over a frame: the click that gave the field focus puts
                    // its caret down after this runs, and would drop a selection
                    // made now. A click into a field selects what is in it; a
                    // second click, with the field already held, moves the caret.
                    dial.SelectPending = true;
                }
                else if (focused && dial.SelectPending)
                {
                    dial.SelectPending = false;
                    dial.Field.selectionAnchorPosition = 0;
                    dial.Field.selectionFocusPosition = dial.Field.text.Length;
                }
                else if (!focused && dial.Editing)
                {
                    dial.Editing = false;
                    dial.SelectPending = false;
                }
                if (focused)
                {
                    any = true;
                }
            }
            HoldInput(any);
        }

        /// <summary>
        /// Besiege must not read the keyboard while a value is being typed, or the
        /// camera walks off on the letters and the block keys fire. Its own menus
        /// raise <c>inMenu</c> for this, and its key handler, the camera and the
        /// selection tools all stand down for it.
        ///
        /// Counted on Besiege's side, so it has to be raised and dropped exactly
        /// once -- hence the flag, and hence dropping it when the panel closes.
        /// </summary>
        private void HoldInput(bool on)
        {
            if (on == typing)
            {
                return;
            }
            typing = on;
            try
            {
                StatMaster.SetInMenu(on);
            }
            catch (Exception)
            {
                typing = false;
            }
        }

        /// <summary>
        /// A value was typed. Unlike a drag this is a finished edit, so it commits
        /// at once rather than waiting for a mouse button that was never held.
        /// Anything unreadable leaves the setting alone and the dial redraws it.
        /// </summary>
        private void Typed(Dial dial, string text)
        {
            if (dial == null)
            {
                return;
            }
            dial.Editing = false;
            if (dial.Bound == null || block == null)
            {
                return;
            }

            float value;
            if (Read(dial, text, out value))
            {
                MSlider bound = dial.Bound;
                if (dial.Step > 0f)
                {
                    value = Mathf.Round(value / dial.Step) * dial.Step;
                }
                bound.Value = Mathf.Clamp(value, bound.Min, bound.Max);
                Commit(bound);
                Refresh();
            }
            ShowDial(dial);
        }

        /// <summary>
        /// Reads back what <see cref="Written"/> puts out, and what someone would
        /// type instead of it: a note as a name or as a number, a time in either
        /// unit it is written in, and a bare number wherever a unit was left off.
        /// </summary>
        private bool Read(Dial dial, string text, out float value)
        {
            value = 0f;
            if (text == null)
            {
                return false;
            }
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            if (dial.Kind == KindNote)
            {
                int midi;
                if (BraidsModels.ParseNote(trimmed, out midi))
                {
                    value = midi;
                    return true;
                }
                return Number(trimmed, out value);
            }

            if (!Number(trimmed, out value))
            {
                return false;
            }
            if (dial.Kind == KindPercent)
            {
                // The dial is written as a percentage, so that is what a bare number
                // typed over it has to mean.
                value = value / 100f;
            }
            else if (dial.Kind == KindSeconds
                     && trimmed.ToLower().IndexOf("ms") >= 0)
            {
                value = value / 1000f;
            }
            return true;
        }

        /// <summary>
        /// The leading number, so the unit can be typed back in with it. Takes a
        /// comma for a decimal point as well as a full stop, and reads the result
        /// the one fixed way -- a box someone types "1.5" into should not depend on
        /// which locale the game came up in.
        /// </summary>
        private static bool Number(string text, out float value)
        {
            value = 0f;
            int start = -1;
            int end = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == ',' || c == '-' || c == '+')
                {
                    if (start < 0) { start = i; }
                    end = i;
                }
                else if (start >= 0)
                {
                    break;
                }
            }
            if (start < 0)
            {
                return false;
            }
            string number = text.Substring(start, end - start + 1).Replace(',', '.');
            return float.TryParse(number,
                                  System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  out value);
        }

        /// <summary>
        /// A dial was dragged. The value goes straight into the block's own mapper
        /// slider, which is what the machine saves -- the panel never holds one.
        /// </summary>
        private void OnDialMoved(Dial dial, float fraction)
        {
            if (dial == null || dial.Bound == null || dial.Writing || block == null)
            {
                return;
            }
            MSlider bound = dial.Bound;
            float value = Mathf.Lerp(DragLow(dial), DragHigh(dial), fraction);
            if (dial.Step > 0f)
            {
                value = Mathf.Round(value / dial.Step) * dial.Step;
            }
            bound.Value = value;
            if (!pending.Contains(bound))
            {
                pending.Add(bound);
            }
            ShowDial(dial);
        }

        private void Update()
        {
            if (block == null || !built || window == null || !window.activeSelf)
            {
                return;
            }

            // A simulation owns the block: the key gates it, the panel does not, and
            // Besiege's own mapper steps aside too rather than floating over the run.
            if (StatMaster.levelSimulating)
            {
                Hide();
                return;
            }

            Typing();

            // Polled rather than bound: ShowModel writes the index itself, and a
            // listener would take that straight back as a change.
            if (modelPicker != null && modelPicker.Index != shownModel
                && block.Model != null)
            {
                ChooseModel(modelPicker.Index);
            }

            // The mapper's own widgets can be moved too, so the panel follows the
            // block rather than assuming it is the only thing writing to it.
            ReadFromBlock();

            if (Time.unscaledTime >= nextScope)
            {
                nextScope = Time.unscaledTime + ScopeInterval;
                int count = block.ReadScope(samples);
                scope.Draw(samples, block.IsPlaying ? count : 0);
            }

            // Committed once, when the drag ends, rather than on every frame of it:
            // each commit reserialises the block and adds an undo entry.
            if (pending.Count > 0 && !Input.GetMouseButton(0))
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    Commit(pending[i]);
                }
                pending.Clear();
                Refresh();
            }
        }

        /// <summary>
        /// Tells Besiege a setting changed, the way its own mapper widgets do.
        ///
        /// <c>OnEditField</c> is the whole ceremony: it applies the value, copies it
        /// to every other block in the selection, reserialises the block so a
        /// simulation and a save see it, and files an undo entry. Falling back to
        /// <c>ApplyValue</c> covers the case where that machinery is not up -- it is
        /// the part that actually makes the setting stick.
        /// </summary>
        private void Commit(MapperType changed)
        {
            if (changed == null)
            {
                return;
            }
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper != null && mapper.Current != null)
                {
                    BlockMapper.OnEditField(mapper.Current, changed);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Warn("could not commit " + changed.Key + " through the mapper ("
                         + e.Message + "); applying it directly.");
            }
            try
            {
                changed.ApplyValue();
            }
            catch (Exception)
            {
                // Nothing further to try; the live value is still set, so the
                // block sounds right even if the setting does not survive a save.
            }
        }

        /// <summary>Redraws Besiege's own widgets, which rebuilds all of them.</summary>
        private void Refresh()
        {
            try
            {
                BlockMapper mapper = BlockMapper.CurrentInstance;
                if (mapper != null)
                {
                    mapper.Refresh();
                }
            }
            catch (Exception)
            {
                // The panel's own values are already right; the mapper's widgets
                // catch up the next time it is opened.
            }
        }

        private void ReadFromBlock()
        {
            // Guarded here rather than relying on the caller: this also runs from
            // Show, and a uGUI callback can close the mapper part-way through a
            // frame, which is what clears the block.
            if (block == null)
            {
                return;
            }
            if (block.Model != null && block.Model.Value != shownModel)
            {
                ShowModel(block.Model.Value);
            }
            for (int i = 0; dials != null && i < dials.Length; i++)
            {
                ShowDial(dials[i]);
            }
            ShowPreview();
        }

        private void ShowModel(int model)
        {
            shownModel = model;
            if (modelPicker != null)
            {
                modelPicker.Set(modelNames, model);
            }

            bool usesTimbre = BraidsModels.UsesTimbre(model);
            bool usesColour = BraidsModels.UsesColour(model);

            if (timbreMeaning != null)
            {
                timbreMeaning.text = "TIMBRE  " + BraidsModels.Timbre(model);
                timbreMeaning.color = usesTimbre ? UIF.QuietInk : Idle;
            }
            if (colourMeaning != null)
            {
                colourMeaning.text = "COLOR  " + BraidsModels.Colour(model);
                colourMeaning.color = usesColour ? UIF.QuietInk : Idle;
            }

            // The dial is left working -- it still writes to the block, and the
            // model can be changed under it -- but a control that does nothing in
            // the model in force should not look as live as one that does.
            Dim(timbre, usesTimbre);
            Dim(colour, usesColour);
        }

        /// <summary>Lettering for a control the chosen model ignores.</summary>
        private static readonly Color Idle = new Color(0.45f, 0.45f, 0.48f, 1f);

        private static void Dim(Dial dial, bool live)
        {
            if (dial == null)
            {
                return;
            }
            Color ink = live ? Color.white : Idle;
            if (dial.Name != null) { dial.Name.color = ink; }
            if (dial.Value != null) { dial.Value.color = ink; }
        }

        private void ShowDial(Dial dial)
        {
            if (dial == null || dial.Bound == null)
            {
                return;
            }
            float value = dial.Bound.Value;
            if (dial.Control != null)
            {
                float low = DragLow(dial);
                float span = DragHigh(dial) - low;
                // Clamped, because a typed value may sit past the end of the travel;
                // the handle rests against the stop and the number tells the truth.
                float fraction = span <= 0f ? 0f : Mathf.Clamp01((value - low) / span);
                if (!Mathf.Approximately(dial.Control.value, fraction))
                {
                    // Flagged, or the control's own callback reads the write back
                    // as the player having moved it.
                    dial.Writing = true;
                    dial.Control.value = fraction;
                    dial.Writing = false;
                }
            }
            // Left alone while it is being typed into, or every frame would undo
            // the keystroke before it was finished.
            if (dial.Editing)
            {
                return;
            }
            string shown = Written(dial, value);
            if (dial.Field != null)
            {
                if (dial.Field.text != shown) { dial.Field.text = shown; }
            }
            else if (dial.Value != null)
            {
                dial.Value.text = shown;
            }
        }

        /// <summary>
        /// How a dial's value reads. A note is worth writing as a note -- 60 means
        /// nothing and C4 means a great deal -- times are worth writing in the unit
        /// they are on the scale of, and TIMBRE and COLOR have no unit at all, so
        /// they are per cent.
        /// </summary>
        private string Written(Dial dial, float value)
        {
            if (dial == note)
            {
                int midi = Mathf.RoundToInt(value);
                return BraidsModels.NoteName(midi) + "  " + midi;
            }
            if (dial == fine)
            {
                int cents = Mathf.RoundToInt(value);
                return (cents > 0 ? "+" : "") + cents + " cents";
            }
            if (dial == attack || dial == release)
            {
                // A gate is set in milliseconds and a swell in seconds; writing both
                // the same way makes one of them unreadable.
                int ms = Mathf.RoundToInt(value * 1000f);
                if (ms < 1000)
                {
                    return ms + " ms";
                }
                // Written out by hand rather than through a format string, which
                // would put the decimal separator of whatever locale the game is in
                // into a box that has to be read back.
                return (ms / 1000) + "." + ((ms % 1000) / 10).ToString("00") + " s";
            }
            if (dial == range)
            {
                return Mathf.RoundToInt(value) + " m";
            }
            if (dial == volume)
            {
                return Mathf.RoundToInt(value * 100f) + "%";
            }
            return Mathf.RoundToInt(value * 100f) + "%";
        }

        /// <summary>
        /// What a field is filled with when it is clicked into. The same as it reads
        /// otherwise, except a note: "C4  60" is a name and a number side by side,
        /// and neither half can be typed back over the pair.
        /// </summary>
        private string Editable(Dial dial, float value)
        {
            if (dial == note)
            {
                return BraidsModels.NoteName(Mathf.RoundToInt(value));
            }
            return Written(dial, value);
        }

        private void ShowPreview()
        {
            bool on = block != null && block.IsPreviewing;
            if (previewMark != null)
            {
                previewMark.color = on ? UIF.Selected : new Color(0f, 0f, 0f, 0f);
            }
            if (previewLabel != null)
            {
                previewLabel.text = on ? "LISTENING" : "LISTEN";
            }
        }
    }
}
