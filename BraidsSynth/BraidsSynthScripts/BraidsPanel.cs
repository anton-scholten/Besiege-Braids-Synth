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

        private const float Width = 520f;
        private const float Margin = 12f;
        private const float ScopeHeight = 96f;
        private const float RowHeight = 26f;
        private const float RowGap = 3f;
        private const int ModelColumns = 2;

        /// <summary>How often the trace is redrawn. Fast enough to look live.</summary>
        private const float ScopeInterval = 0.05f;

        private static readonly Vector2 Reference = new Vector2(1920f, 1080f);

        private BraidsBehaviour block;
        private bool hooked;
        private bool built;
        private bool failed;

        private GameObject window;
        private RectTransform windowRect;
        private ClickShield shield;

        private RawImage scopeImage;
        private Scope scope;
        private float[] samples;
        private float nextScope;

        private readonly Image[] modelMarks = new Image[BraidsModels.Count];
        private readonly Text[] modelLabels = new Text[BraidsModels.Count];
        private int shownModel = -1;

        private Text timbreMeaning;
        private Text colourMeaning;
        private Text previewLabel;
        private Image previewMark;

        private Dial note;
        private Dial fine;
        private Dial timbre;
        private Dial colour;
        private Dial volume;

        /// <summary>
        /// A row of the panel: a name, one of UI Factory's sliders, and the value
        /// written out. Bound to one of the block's mapper sliders, which is what the
        /// machine saves -- the panel never keeps a value of its own.
        /// </summary>
        private class Dial
        {
            public UnityEngine.UI.Slider Control;
            public Text Value;
            public Text Name;
            public MSlider Bound;
            public bool Writing;

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
            // Asked for, not insisted on: UI Factory's Window places itself when it
            // is enabled, and where it puts itself -- centred, which lands just left
            // of Besiege's block mapper -- is where it stays. Its own top bar is a
            // drag handle, and so is the trace, so the player can move it anyway.
            windowRect.anchorMin = new Vector2(0.5f, 0.5f);
            windowRect.anchorMax = new Vector2(0.5f, 0.5f);
            windowRect.pivot = new Vector2(0.5f, 0.5f);

            RectTransform bar = window.transform.FindChild("TopBar") as RectTransform;
            if (bar != null)
            {
                Text title = bar.GetComponentInChildren<Text>(true);
                if (title != null)
                {
                    UIF.Untranslate(title);
                    title.text = "BRAIDS";
                    title.alignment = TextAnchor.MiddleCenter;
                    title.raycastTarget = false;
                }
                Transform close = bar.FindChild("CloseButton");
                if (close != null)
                {
                    // The mapper is what owns this window's life, so its own cross
                    // closes the mapper rather than orphaning the panel.
                    Button button = close.GetComponent<Button>();
                    if (button != null)
                    {
                        button.onClick.AddListener(CloseMapper);
                    }
                }
            }

            shield = gameObject.GetComponent<ClickShield>();
            if (shield == null)
            {
                shield = gameObject.AddComponent<ClickShield>();
            }
            shield.Guard(windowRect);

            float y = bar == null ? Margin : bar.rect.height + Margin;
            y = BuildScope(y);
            y = BuildModels(y);
            y = BuildMeanings(y);
            y = BuildDials(y);
            y = BuildPreview(y);

            // The window is as tall as what went into it. Guessing a height means
            // the last rows hang below the frame the moment the model list, the
            // prefab's top bar or a row height changes -- and every child is
            // anchored to the top edge, so growing it downwards leaves them put.
            windowRect.sizeDelta = new Vector2(Width, y);

            window.SetActive(false);
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
            GameObject go = UIF.Spawn(UIF.TextPrefab, window.transform);
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
            go.transform.SetParent(window.transform, false);
            Place(go, Margin, y, Width - Margin * 2f, ScopeHeight);

            scopeImage = go.AddComponent<RawImage>();
            scope = new Scope(Mathf.RoundToInt(Width - Margin * 2f),
                              Mathf.RoundToInt(ScopeHeight));
            scopeImage.texture = scope.Texture;
            scopeImage.raycastTarget = true;
            samples = new float[BraidsBehaviour.ScopeSize];

            // The trace is a big quiet area, which makes it the natural second place
            // to pick the window up by.
            UIF.Draggable(go, windowRect);

            return y + ScopeHeight + Margin;
        }

        private float BuildModels(float y)
        {
            Label("MODEL", Margin, y, 200f, 18f, 12, TextAnchor.MiddleLeft, UIF.QuietInk);
            y += 20f;

            int rows = (BraidsModels.Count + ModelColumns - 1) / ModelColumns;
            float columnWidth = (Width - Margin * 2f - RowGap * (ModelColumns - 1))
                                / ModelColumns;

            for (int i = 0; i < BraidsModels.Count; i++)
            {
                int column = i / rows;
                int row = i % rows;

                GameObject button = UIF.Spawn(UIF.ButtonPrefab, window.transform);
                if (button == null)
                {
                    continue;
                }
                button.name = BraidsModels.Name(i);
                Place(button, Margin + column * (columnWidth + RowGap),
                      y + row * (RowHeight + RowGap), columnWidth, RowHeight);
                UIF.NoSwell(button);

                // The prefab's own background cannot be reliably tinted -- UI
                // Factory draws it with a custom shader that need not multiply by
                // the renderer's colour -- so the mark is an Image of ours behind
                // the label, borrowing the prefab's sprite to keep its corners.
                Image face = button.GetComponent<Image>();
                GameObject markObject = new GameObject("Mark", typeof(RectTransform));
                markObject.transform.SetParent(button.transform, false);
                RectTransform markRect = markObject.transform as RectTransform;
                markRect.anchorMin = Vector2.zero;
                markRect.anchorMax = Vector2.one;
                markRect.offsetMin = Vector2.zero;
                markRect.offsetMax = Vector2.zero;
                markRect.SetAsFirstSibling();

                Image mark = markObject.AddComponent<Image>();
                if (face != null)
                {
                    mark.sprite = face.sprite;
                    mark.type = face.type;
                }
                mark.color = new Color(0f, 0f, 0f, 0f);
                mark.raycastTarget = false;
                modelMarks[i] = mark;

                Text label = button.GetComponentInChildren<Text>(true);
                if (label != null)
                {
                    UIF.Untranslate(label);
                    label.text = BraidsModels.Name(i).ToUpper();
                    label.fontSize = 12;
                    label.resizeTextForBestFit = false;
                    label.alignment = TextAnchor.MiddleCenter;
                    label.horizontalOverflow = HorizontalWrapMode.Overflow;
                    RectTransform labelRect = label.rectTransform;
                    if (labelRect != button.transform)
                    {
                        labelRect.anchorMin = Vector2.zero;
                        labelRect.anchorMax = Vector2.one;
                        labelRect.offsetMin = Vector2.zero;
                        labelRect.offsetMax = Vector2.zero;
                    }
                }
                modelLabels[i] = label;

                Button click = button.GetComponent<Button>();
                if (click != null)
                {
                    int chosen = i;
                    click.onClick.AddListener(delegate { ChooseModel(chosen); });
                }
            }

            return y + rows * (RowHeight + RowGap) + Margin;
        }

        private float BuildMeanings(float y)
        {
            timbreMeaning = Label("", Margin, y, Width - Margin * 2f, 16f, 12,
                                  TextAnchor.MiddleLeft, UIF.QuietInk);
            y += 18f;
            colourMeaning = Label("", Margin, y, Width - Margin * 2f, 16f, 12,
                                  TextAnchor.MiddleLeft, UIF.QuietInk);
            return y + 18f + Margin;
        }

        private float BuildDials(float y)
        {
            note = BuildDial("NOTE", y);
            note.Step = 1f;
            y += RowHeight + RowGap;
            fine = BuildDial("FINE", y);
            fine.Step = 1f;
            y += RowHeight + RowGap;
            timbre = BuildDial("TIMBRE", y);
            y += RowHeight + RowGap;
            colour = BuildDial("COLOR", y);
            y += RowHeight + RowGap;
            volume = BuildDial("VOLUME", y);
            return y + RowHeight + Margin;
        }

        private const float DialNameWidth = 74f;
        private const float DialValueWidth = 96f;

        private Dial BuildDial(string name, float y)
        {
            Dial dial = new Dial();
            dial.Name = Label(name, Margin, y, DialNameWidth, RowHeight, 12,
                              TextAnchor.MiddleLeft, Color.white);

            float left = Margin + DialNameWidth;
            float right = Width - Margin - DialValueWidth;

            GameObject go = UIF.Spawn(UIF.SliderPrefab, window.transform);
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

            dial.Value = Label("", right, y, DialValueWidth, RowHeight, 12,
                               TextAnchor.MiddleRight, Color.white);
            return dial;
        }

        private float BuildPreview(float y)
        {
            GameObject button = UIF.Spawn(UIF.ButtonPrefab, window.transform);
            if (button == null)
            {
                return y;
            }
            button.name = "Preview";
            Place(button, Margin, y, Width - Margin * 2f, PreviewHeight);
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

        private void CloseMapper()
        {
            try
            {
                BlockMapper.Close();
            }
            catch (Exception)
            {
                Hide();
            }
        }

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
            float value = Mathf.Lerp(bound.Min, bound.Max, fraction);
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
            if (block.IsSimulating)
            {
                Hide();
                return;
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
            ShowDial(note);
            ShowDial(fine);
            ShowDial(timbre);
            ShowDial(colour);
            ShowDial(volume);
            ShowPreview();
        }

        private void ShowModel(int model)
        {
            shownModel = model;
            for (int i = 0; i < modelMarks.Length; i++)
            {
                bool chosen = i == model;
                if (modelMarks[i] != null)
                {
                    modelMarks[i].color = chosen
                        ? UIF.Selected
                        : new Color(0f, 0f, 0f, 0f);
                }
                if (modelLabels[i] != null)
                {
                    // Braids' own models first, then the raw waveforms, which are
                    // not models and are written quieter to say so.
                    modelLabels[i].color = chosen
                        ? Color.white
                        : (i < BraidsModels.WaveformsFrom ? Color.white : UIF.QuietInk);
                }
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
                float span = dial.Bound.Max - dial.Bound.Min;
                float fraction = span <= 0f ? 0f : (value - dial.Bound.Min) / span;
                if (!Mathf.Approximately(dial.Control.value, fraction))
                {
                    // Flagged, or the control's own callback reads the write back
                    // as the player having moved it.
                    dial.Writing = true;
                    dial.Control.value = fraction;
                    dial.Writing = false;
                }
            }
            if (dial.Value != null)
            {
                dial.Value.text = Written(dial, value);
            }
        }

        /// <summary>
        /// How a dial's value reads. A note is worth writing as a note -- 60 means
        /// nothing and C4 means a great deal -- and TIMBRE and COLOR have no unit at
        /// all, so they are per cent.
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
            if (dial == volume)
            {
                return Mathf.RoundToInt(value * 100f) + "%";
            }
            return Mathf.RoundToInt(value * 100f) + "%";
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
