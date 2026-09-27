using System.Collections.Generic;
using AirTools.Parts;
using AirTools.UI;
using UnityEditor;
using UnityEngine;

namespace AirTools.Editor
{
    /// catalog: the Catalog window and its glass keyboard, built with UiBuild (docs/UI.md) so the base components' look
    /// (liquid glass) applies. Called from MainSceneBuilder.WireTools in place of the old crate menu: the same PartsMenu
    /// object in the left side slot (0.5 m, 15° down, 35° left; UiZones.SideLeft, 0.30 × 0.40 m), with PartsBrowser under
    /// it as the parts engine and CatalogWindow drawing it. Idempotent (the rig's PartsMenu is replaced).
    ///   Title row: "Catalog · Kitchen" · Talk · Close
    ///   Search field (tap: the keyboard) · focus chip "Looking at: the roof ⌄" (gaze-catalog: tap to pin / unpin)
    ///   Category chips: 2 rows of 4 (Results first after a live search; More… pages the rest)
    ///   Fits 2′ 4⅝″ chip (with a tape or gap) · status line
    ///   Grid: 2 × 3 cards (photo, name, "$459 · 23⅞″ W", 3D badge) · ← 1 / 3 →
    ///   Keyboard (hangs below the window, tilted back 25°, 4 cm nearer): digits, QWERTY, "-", "\"", Delete, Clear,
    ///   space, Search. Every key 3 cm (docs/UI.md §6 targets), poke + ray (D5).
    public static class CatalogBuilder
    {
        public const string Name = "PartsMenu";
        public const float W = 0.30f, H = 0.40f, Distance = 0.5f;
        public const int Chips = CatalogModel.ChipSlots, Cards = CatalogModel.CardsPerPage, Columns = 3;
        public static readonly Vector2 CardSize = new Vector2(0.0853f, 0.086f);
        public static readonly Vector2 ChipSize = new Vector2(0.064f, 0.026f);
        /// gaze-catalog: the focus chip at the search field's right.
        public static readonly Vector2 FocusChipSize = new Vector2(0.124f, 0.032f);
        public const float KeyUnit = 0.03f, KeyGap = 0.004f, KeyPad = 0.008f, KeyTilt = 25f, KeyNearer = 0.04f;
        public const float KeyCooldown = 0.1f;

        /// The keyboard panel's size (10 key units wide, 5 rows).
        public static Vector2 KeyboardSize => new Vector2(10 * KeyUnit + 9 * KeyGap + 2 * KeyPad,
            CatalogTextField.Rows.Length * KeyUnit + (CatalogTextField.Rows.Length - 1) * KeyGap + 2 * KeyPad);

        public static CatalogWindow Build(GameObject app, OVRCameraRig rig, GameObject template, Shader unlit,
            PartsClient client, PartLoader loader, PartTool tool, PartCatalog shipped)
        {
            var old = rig.transform.Find(Name);
            if (old != null) Object.DestroyImmediate(old.gameObject);
            UiBuild.Distance = Distance;   // read from its side slot, 0.5 m away
            UiBuild.D5Rays = true;         // poke, and a hand or controller ray (D5)
            var go = new GameObject(Name);
            go.transform.SetParent(rig.transform, false);
            // Built pose for an untracked head; opened later in the left side slot from the person (UX W0.7).
            go.transform.localPosition = new Vector3(-0.24f, 1.40f, 0.37f);
            go.transform.localRotation = Quaternion.Euler(15f, -32f, 0f);
            var browser = go.AddComponent<PartsBrowser>();
            browser.client = client; browser.loader = loader; browser.tool = tool;
            browser.head = rig.centerEyeAnchor;
            var catalogClient = app.GetComponent<CatalogClient>();   // (not ??: a missing component is Unity's fake null)
            if (catalogClient == null) catalogClient = app.AddComponent<CatalogClient>();
            var window = go.AddComponent<CatalogWindow>();
            window.browser = browser; window.client = catalogClient; window.parts = client; window.shipped = shipped;
            browser.catalog = window;
            var content = new GameObject("Content");
            content.transform.SetParent(go.transform, false);
            browser.content = content;
            var c = content.transform;
            browser.panel = UiBuild.Panel(c, "Panel", new Vector2(W, H));
            browser.panelTop = H * 0.5f;
            float x0 = -W * 0.5f + 0.016f, top = H * 0.5f, inner = W - 0.032f;

            // Title row: title, Talk (voice stays: what you say fills the search field), Close.
            var closeSize = new Vector2(0.05f, 0.026f);
            var talkSize = new Vector2(0.075f, 0.028f);
            float closeX = W * 0.5f - 0.012f - closeSize.x * 0.5f, talkX = closeX - closeSize.x * 0.5f - 0.004f - talkSize.x * 0.5f;
            float rowY = top - 0.026f;
            window.title = UiBuild.Text(c, "Title", "Catalog", TypeRole.Heading, new Vector3(x0, rowY, 0f), width: talkX - talkSize.x * 0.5f - 0.006f - x0);
            window.title.enableWordWrapping = false;
            window.title.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            browser.talk = UiBuild.Button(c, "Talk", AirTools.Agent.TalkButton.IdleText, talkSize, ButtonStyle.Primary, template,
                new Vector3(talkX, rowY, 0f), TypeRole.Caption);
            window.close = UiBuild.Button(c, "Close", "Close", closeSize, ButtonStyle.Borderless, template, new Vector3(closeX, rowY, 0f), TypeRole.Caption);
            Bind(window.close, window, CatalogAction.Close);

            // The search field: a glass field (the keyboard's target; selected = ink while typing), a magnifying glass.
            // gaze-catalog: it shares its row with the focus chip (what you look at; tap to pin).
            var fieldSize = new Vector2(inner - FocusChipSize.x - 0.004f, 0.032f);
            float fieldY = top - 0.066f;
            window.field = UiBuild.Button(c, "SearchField", "", fieldSize, ButtonStyle.Secondary, template, new Vector3(x0 + fieldSize.x * 0.5f, fieldY, 0f),
                TypeRole.Label, RadiusRole.Pill);
            Bind(window.field, window, CatalogAction.Field);
            float fx = -fieldSize.x * 0.5f;
            Icon(window.field.visual, "Icon", Icons.Search, 0.012f, new Vector3(fx + 0.013f, 0f, -0.0015f));
            window.fieldText = UiBuild.Text(window.field.visual, "Text", "Search…", TypeRole.Label, new Vector3(fx + 0.026f, 0f, -0.0015f),
                ColorRole.TextSecondary, width: fieldSize.x - 0.034f);
            window.fieldText.enableWordWrapping = false;
            window.fieldText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            // gaze-catalog: the focus chip.
            window.focusChip = UiBuild.Button(c, "FocusChip", "All categories", FocusChipSize, ButtonStyle.Chip, template,
                new Vector3(W * 0.5f - 0.016f - FocusChipSize.x * 0.5f, fieldY, 0f), TypeRole.Caption, RadiusRole.Pill);
            window.focusChip.label.enableWordWrapping = false;
            window.focusChip.label.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            window.focusChip.label.rectTransform.sizeDelta = new Vector2(FocusChipSize.x - 0.010f, window.focusChip.label.rectTransform.sizeDelta.y);
            Bind(window.focusChip, window, CatalogAction.FocusPin);

            // Category chips: two rows of four.
            var chips = new List<GlassButton>();
            for (int i = 0; i < Chips; i++)
            {
                int row = i / 4, col = i % 4;
                var pos = new Vector3(x0 + ChipSize.x * 0.5f + col * (ChipSize.x + 0.004f), top - 0.103f - row * 0.030f, 0f);
                var b = UiBuild.Button(c, $"Chip{i}", "Category", ChipSize, ButtonStyle.Chip, template, pos, TypeRole.Caption, RadiusRole.Pill);
                b.label.enableWordWrapping = false;
                b.label.overflowMode = TMPro.TextOverflowModes.Ellipsis;
                Bind(b, window, CatalogAction.Chip, i);
                chips.Add(b);
            }
            window.chips = chips.ToArray();

            // Fits chip (only with a tape or gap) and the status line beside it.
            float statusY = top - 0.164f;
            var fitsSize = new Vector2(0.092f, 0.026f);
            window.fits = UiBuild.Button(c, "Fits", "Fits", fitsSize, ButtonStyle.Chip, template, new Vector3(x0 + fitsSize.x * 0.5f, statusY, 0f),
                TypeRole.Caption, RadiusRole.Pill);
            Bind(window.fits, window, CatalogAction.Fits);
            window.fits.gameObject.SetActive(false);
            window.statusXAlone = x0;
            window.statusXWithFits = x0 + fitsSize.x + 0.006f;
            window.statusRight = W * 0.5f - 0.016f;
            window.status = UiBuild.Text(c, "Status", "", TypeRole.Caption, new Vector3(x0, statusY, 0f), ColorRole.TextSecondary, width: inner);
            window.status.enableWordWrapping = false;
            window.status.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            browser.statusText = window.status;   // the live search's progress and "Aim at …, pinch to place" show here too

            // The grid: 2 rows × 3 cards.
            var photoMat = Material("PartPhoto", unlit);
            var cards = new List<CatalogCardView>();
            float pitchX = CardSize.x + 0.006f, gridTop = statusY - 0.019f;
            for (int i = 0; i < Cards; i++)
            {
                int row = i / Columns, col = i % Columns;
                var pos = new Vector3((col - (Columns - 1) * 0.5f) * pitchX, gridTop - CardSize.y * 0.5f - row * (CardSize.y + 0.006f), 0f);
                cards.Add(Card(c, i, pos, template, photoMat, window));
            }
            window.cards = cards.ToArray();
            window.emptyButton = UiBuild.Button(c, "SearchStores", "Search the stores", new Vector2(0.2f, 0.032f), ButtonStyle.Primary, template,
                new Vector3(0f, gridTop - CardSize.y - 0.003f, 0f), TypeRole.Caption, RadiusRole.Pill);
            Bind(window.emptyButton, window, CatalogAction.EmptySearch);
            window.emptyButton.gameObject.SetActive(false);

            // Pager.
            float pagerY = -H * 0.5f + 0.022f;
            var arrow = new Vector2(0.05f, 0.026f);
            window.previous = UiBuild.Button(c, "Previous", "←", arrow, ButtonStyle.Secondary, template, new Vector3(-0.07f, pagerY, 0f), TypeRole.Title, RadiusRole.Pill);
            Bind(window.previous, window, CatalogAction.Previous);
            window.next = UiBuild.Button(c, "Next", "→", arrow, ButtonStyle.Secondary, template, new Vector3(0.07f, pagerY, 0f), TypeRole.Title, RadiusRole.Pill);
            Bind(window.next, window, CatalogAction.Next);
            window.pageText = UiBuild.Text(c, "Page", "1 / 1", TypeRole.Caption, new Vector3(0f, pagerY, 0f), ColorRole.TextSecondary,
                TMPro.TextAlignmentOptions.Center);
            window.pageText.rectTransform.sizeDelta = new Vector2(0.08f, 0.016f);

            browser.handle = UiBuild.Handle(go.transform, rig.centerEyeAnchor, new Vector3(0f, -H * 0.5f - 0.016f, 0f), template);
            browser.handle.transform.SetParent(c, true);

            window.keyboard = BuildKeyboard(c, template, window);
            // gaze-catalog: what the wearer looks at, ~4 times a second; reading this window (or its keyboard) pauses it.
            var gaze = go.AddComponent<GazeFocusTracker>();
            gaze.window = window;
            gaze.head = rig.centerEyeAnchor;
            gaze.panelSize = new Vector2(W, H);
            gaze.keyboardDrop = 0.034f + KeyboardSize.y * Mathf.Cos(KeyTilt * Mathf.Deg2Rad) + 0.02f;
            window.gaze = gaze;
            content.SetActive(false);
            UiBuild.D5Rays = false;
            return window;
        }

        /// One card: a glass button (tap = take), the photo above its two-line name, "$459 · 23⅞″ W" and a 3D badge.
        static CatalogCardView Card(Transform parent, int i, Vector3 pos, GameObject template, Material photoMat, CatalogWindow window)
        {
            var b = UiBuild.Button(parent, $"Card{i}", "", CardSize, ButtonStyle.Secondary, template, pos, TypeRole.Caption, RadiusRole.Medium);
            var view = b.gameObject.AddComponent<CatalogCardView>();
            view.button = b;
            b.label.transform.localPosition = new Vector3(0f, -0.012f, -0.0015f);
            b.label.rectTransform.sizeDelta = new Vector2(CardSize.x - 0.006f, 0.022f);
            b.label.enableWordWrapping = true;
            b.label.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            var photo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            photo.name = "Photo";
            Object.DestroyImmediate(photo.GetComponent<Collider>());
            photo.transform.SetParent(b.visual, false);
            photo.transform.localPosition = new Vector3(0f, 0.019f, -0.0012f);
            photo.transform.localScale = new Vector3(0.042f, 0.042f, 1f);
            var mr = photo.GetComponent<MeshRenderer>();
            mr.sharedMaterial = photoMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            view.photo = mr;
            view.detail = UiBuild.Text(b.visual, "Detail", "", TypeRole.Caption, new Vector3(0f, -0.033f, -0.0015f), ColorRole.TextSecondary,
                TMPro.TextAlignmentOptions.Center, weight: Weight.Medium);
            view.detail.rectTransform.sizeDelta = new Vector2(CardSize.x - 0.006f, 0.012f);
            view.detail.enableWordWrapping = false;
            view.detail.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            // "3D": the model is ready (a dark pill over the photo's corner; words, not colour alone).
            var badge = new GameObject("Badge3D");
            badge.transform.SetParent(b.visual, false);
            badge.transform.localPosition = new Vector3(CardSize.x * 0.5f - 0.014f, CardSize.y * 0.5f - 0.009f, -0.002f);
            var pill = GlassSurface.Create(badge.transform, "Pill", new Vector2(0.02f, 0.012f), GlassTier.ElevatedSolid, RadiusRole.Pill);
            pill.layer = GlassSurface.LayerIndicator;
            pill.Rebuild();
            UiBuild.Text(badge.transform, "Text", "3D", TypeRole.Caption, new Vector3(0f, 0f, -0.0008f), ColorRole.TextPrimary,
                TMPro.TextAlignmentOptions.Center, weight: Weight.Semibold).rectTransform.sizeDelta = new Vector2(0.02f, 0.012f);
            badge.SetActive(false);
            view.badge = badge;
            Bind(b, window, CatalogAction.Card, i);
            b.gameObject.SetActive(false);
            return view;
        }

        /// The glass keyboard: under the window, tilted back like a desk and a little nearer, hidden until the field is
        /// tapped. Keys from CatalogTextField.Rows; Search is the primary; Delete shows Phosphor's backspace.
        static CatalogKeyboard BuildKeyboard(Transform windowContent, GameObject template, CatalogWindow window)
        {
            var root = new GameObject("Keyboard");
            root.transform.SetParent(windowContent, false);
            root.transform.localPosition = new Vector3(0f, -H * 0.5f - 0.034f, -KeyNearer);
            root.transform.localRotation = Quaternion.Euler(KeyTilt, 0f, 0f);   // top edge away from you, bottom nearer
            var kb = root.AddComponent<CatalogKeyboard>();
            var content = new GameObject("Content");
            content.transform.SetParent(root.transform, false);
            kb.content = content;
            var size = KeyboardSize;
            UiBuild.Panel(content.transform, "KeyboardPanel", size, localPos: new Vector3(0f, -size.y * 0.5f, 0f));
            var keys = new List<CatalogButton>();
            var theme = UiTheme.Current;
            for (int r = 0; r < CatalogTextField.Rows.Length; r++)
            {
                var row = CatalogTextField.Rows[r];
                int units = 0;
                foreach (var k in row) units += CatalogTextField.Units(k);
                float rowWidth = units * KeyUnit + (units - 1) * KeyGap;
                float x = -rowWidth * 0.5f, y = -KeyPad - KeyUnit * 0.5f - r * (KeyUnit + KeyGap);
                foreach (var k in row)
                {
                    int u = CatalogTextField.Units(k);
                    float w = u * KeyUnit + (u - 1) * KeyGap;
                    bool icon = k == CatalogTextField.Back && theme.icons.regular != null;
                    var style = k == CatalogTextField.Enter ? ButtonStyle.Primary : ButtonStyle.Secondary;
                    var b = UiBuild.Button(content.transform, $"Key_{KeyName(k)}", icon ? "" : CatalogTextField.Label(k), new Vector2(w, KeyUnit), style, template,
                        new Vector3(x + w * 0.5f, y, 0f), TypeRole.Label, RadiusRole.Small);
                    b.cooldownSeconds = KeyCooldown;   // typing "ee": a key repeats faster than the 0.3 s button default
                    if (icon) Icon(b.visual, "Icon", Icons.Backspace, 0.016f, new Vector3(0f, 0f, -0.0015f));
                    var cb = Bind(b, window, CatalogAction.Key);
                    cb.key = k;
                    keys.Add(cb);
                    x += w + KeyGap;
                }
            }
            kb.keys = keys.ToArray();
            content.SetActive(false);
            return kb;
        }

        static string KeyName(string k) => k switch { "\"" => "quote", "-" => "dash", _ => k };

        static CatalogButton Bind(GlassButton b, CatalogWindow window, CatalogAction action, int index = 0)
        {
            var cb = b.gameObject.AddComponent<CatalogButton>();
            cb.button = b; cb.window = window; cb.action = action; cb.index = index;
            return cb;
        }

        /// A Phosphor glyph (em size in metres), centred (the ring's icon look: soft underlay, cool gradient).
        static TMPro.TextMeshPro Icon(Transform parent, string name, string glyph, float em, Vector3 localPos)
        {
            var theme = UiTheme.Current;
            var t = UiBuild.Text(parent, name, glyph, TypeRole.Title, localPos, ColorRole.TextPrimary, TMPro.TextAlignmentOptions.Center);
            if (theme.icons.regular != null) t.font = theme.icons.regular;
            if (theme.icons.regularMaterial != null) t.fontSharedMaterial = theme.icons.regularMaterial;
            t.fontSize = em / 0.1f;
            t.enableWordWrapping = false;
            t.rectTransform.sizeDelta = new Vector2(em * 1.6f, em * 1.6f);
            return t;
        }

        static Material Material(string name, Shader shader)
        {
            const string folder = "Assets/AirTools/Materials";
            System.IO.Directory.CreateDirectory(folder);
            string path = $"{folder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = shader;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
