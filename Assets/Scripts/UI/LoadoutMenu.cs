using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Gunbound
{
    /// <summary>
    /// Quick loadout menu (opened with the Loadout key, B by default).
    ///  - List page: the 5 loadouts. Click a bar to select it, click EDIT to customise it.
    ///  - Edit page (replaces the list): rename the loadout and pick a weapon for every slot in LoadoutSchema.
    /// Pages are rebuilt from scratch every time they are shown, so layout changes only touch BuildList/BuildEdit.
    /// Weapons/slots come from WeaponCatalog and LoadoutSchema — adding either needs no change here.
    /// </summary>
    public class LoadoutMenu
    {
        // ---- look ----
        static readonly Vector2 WindowSize = new(900, 940);
        static readonly Vector2 MusicSize = new(700, 940);
        const float WeaponCardHeight = 150f;
        const float PerkBoxHeight = 120f;
        const float PanelGap = 20f;
        const float RowHeight = 104f;
        const float EditWidth = 150f;
        static readonly Color SelectedRowCol = new(0.30f, 0.20f, 0.08f, 1f);
        static readonly Color ChosenCardCol = new(0.33f, 0.22f, 0.09f, 1f);

        public bool IsOpen { get; private set; }
        /// <summary>True while the player is typing in a field (so letter keys like B don't close the menu).</summary>
        public static bool IsTyping
        {
            get
            {
                var go = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                var f = go != null ? go.GetComponent<InputField>() : null;
                return f != null && f.isFocused;
            }
        }

        readonly RectTransform _root, _window;
        readonly Action _onVisibilityChanged;
        RectTransform _page;
        int _editing = -1;
        bool _pickerOpen; // a weapon/perk picker is showing over the edit page
        bool _faceOpen;   // the face editor page is showing

        public LoadoutMenu(Transform canvas, Action onVisibilityChanged)
        {
            _onVisibilityChanged = onVisibilityChanged;
            var dim = UIFactory.Image("LoadoutMenu", canvas, new Color(0, 0, 0, 0.55f));
            _root = UIFactory.Stretch(dim.rectTransform);
            // loadouts on the left, music panel on the right
            float total = WindowSize.x + PanelGap + MusicSize.x;
            _window = UIFactory.Image("Window", _root, UIFactory.PanelCol).rectTransform;
            _windowWithMusic = new Vector2(-total / 2 + WindowSize.x / 2, 0);
            UIFactory.Place(_window, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), _windowWithMusic, WindowSize);
            _music = MusicPanel.Create(_root, new Vector2(total / 2 - MusicSize.x / 2, 0), MusicSize).gameObject;
            _root.gameObject.SetActive(false);
        }

        readonly GameObject _music;
        readonly Vector2 _windowWithMusic;

        /// <summary>The music panel only makes sense in a game; without it the loadout window is centred.</summary>
        public void SetMusicVisible(bool on)
        {
            if (_music.activeSelf != on) _music.SetActive(on);
            _window.anchoredPosition = on ? _windowWithMusic : Vector2.zero;
        }

        // ------------------------------------------------------------------ open / close

        public void Open()
        {
            IsOpen = true;
            ShowList();
            _root.gameObject.SetActive(true);
            _onVisibilityChanged?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _editing = -1;
            _faceOpen = false;
            WeaponPreview.SetRendering(false);
            FacePreview.SetRendering(false);
            _root.gameObject.SetActive(false);
            _onVisibilityChanged?.Invoke();
        }

        public void Toggle() { if (IsOpen) Close(); else Open(); }

        /// <summary>Hide without forgetting it's open (e.g. while the pause menu covers it).</summary>
        public void SetShown(bool shown)
        {
            _root.gameObject.SetActive(IsOpen && shown);
            WeaponPreview.SetRendering(IsOpen && shown && _editing >= 0);
            FacePreview.SetRendering(IsOpen && shown && _faceOpen);
        }

        /// <summary>Esc: perk picker closes, edit/face page goes back to the list, list closes the menu.</summary>
        public void Back()
        {
            if (_editing >= 0 && _pickerOpen) ShowEdit(_editing);
            else if (_editing >= 0 || _faceOpen) ShowList();
            else Close();
        }

        // ------------------------------------------------------------------ pages

        RectTransform NewPage()
        {
            if (_page != null) UnityEngine.Object.Destroy(_page.gameObject);
            _page = UIFactory.Stretch(UIFactory.Rect("Page", _window));
            return _page;
        }

        void ShowList()
        {
            _editing = -1;
            _pickerOpen = false;
            _faceOpen = false;
            WeaponPreview.SetRendering(false);
            FacePreview.SetRendering(false);
            BuildList(NewPage());
        }

        void ShowFace()
        {
            _editing = -1;
            _pickerOpen = false;
            _faceOpen = true;
            WeaponPreview.SetRendering(false);
            BuildFace(NewPage());
            FacePreview.SetRendering(true);
        }

        void ShowEdit(int index)
        {
            _editing = index;
            _pickerOpen = false;
            _faceOpen = false;
            BuildEdit(NewPage(), index);
            WeaponPreview.SetRendering(true);
        }

        void ShowPerkPicker(int index, int slot)
        {
            ShowEdit(index);
            _pickerOpen = true;
            BuildPerkPicker(_page, index, slot);
        }

        void ShowWeaponPicker(int index, LoadoutSlot slot)
        {
            ShowEdit(index);
            _pickerOpen = true;
            BuildWeaponPicker(_page, index, slot);
        }

        void BuildList(RectTransform page)
        {
            Header(page, "LOADOUTS", NextSpawnHint());

            var list = UIFactory.Rect("List", page);
            UIFactory.Place(list, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -130), new Vector2(WindowSize.x - 60, Loadouts.Count * (RowHeight + 12)));
            UIFactory.VList(list, 12);

            for (int i = 0; i < Loadouts.Count; i++)
            {
                int idx = i;
                var l = Loadouts.Get(i);
                bool selected = Loadouts.SelectedIndex == i;

                var row = UIFactory.Rect("Loadout" + (i + 1), list);
                UIFactory.Size(row, RowHeight);

                // the bar itself selects the loadout
                var bar = UIFactory.Button("Select", row, "", () => { Loadouts.Select(idx); ShowList(); });
                var barRt = (RectTransform)bar.transform;
                UIFactory.Stretch(barRt, 0, 0, EditWidth + 10, 0);
                ((Image)bar.targetGraphic).color = selected ? SelectedRowCol : UIFactory.ButtonCol;
                if (selected)
                {
                    var stripe = UIFactory.Image("Stripe", barRt, UIFactory.Accent);
                    stripe.raycastTarget = false;
                    stripe.rectTransform.anchorMin = new Vector2(0, 0);
                    stripe.rectTransform.anchorMax = new Vector2(0, 1);
                    stripe.rectTransform.pivot = new Vector2(0, 0.5f);
                    stripe.rectTransform.sizeDelta = new Vector2(6, 0);
                    stripe.rectTransform.anchoredPosition = Vector2.zero;
                }
                var name = UIFactory.Text("Name", barRt, l.name, 32, TextAnchor.MiddleCenter, selected ? UIFactory.Accent : UIFactory.TextCol, FontStyle.Bold);
                UIFactory.Stretch(name.rectTransform, 140, 0, 140, 0); // clear of the SELECTED tag
                if (selected)
                {
                    var tag = UIFactory.Text("Selected", barRt, "SELECTED", 20, TextAnchor.MiddleRight, UIFactory.Accent, FontStyle.Bold);
                    UIFactory.Stretch(tag.rectTransform, 0, 0, 24, 0);
                }

                // EDIT opens the customisation page
                var edit = UIFactory.Button("Edit", row, "EDIT", () => ShowEdit(idx), 24);
                var er = (RectTransform)edit.transform;
                er.anchorMin = new Vector2(1, 0);
                er.anchorMax = new Vector2(1, 1);
                er.pivot = new Vector2(1, 0.5f);
                er.sizeDelta = new Vector2(EditWidth, 0);
                er.anchoredPosition = Vector2.zero;
            }

            // face: one bar under the loadouts, with a thumbnail of the current drawing
            var face = UIFactory.Button("Face", page, "", ShowFace);
            var fr = (RectTransform)face.transform;
            UIFactory.Place(fr, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 100), new Vector2(WindowSize.x - 60, 96));
            var thumbBg = UIFactory.Image("Thumb", fr, FacePreview.SkinColor);
            thumbBg.raycastTarget = false;
            UIFactory.Place(thumbBg.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(12, 0), new Vector2(76, 76));
            FacePreview.Painter.Draw(FaceArt.Strokes);
            var thumb = UIFactory.Rect("Drawing", thumbBg.rectTransform).gameObject.AddComponent<RawImage>();
            thumb.texture = FacePreview.Painter.Texture;
            thumb.raycastTarget = false;
            UIFactory.Stretch(thumb.rectTransform);
            var ft = UIFactory.Text("Label", fr, "FACE  <color=#9aa0aa><size=18>draw what's on your character's head</size></color>",
                30, TextAnchor.MiddleLeft, UIFactory.TextCol, FontStyle.Bold);
            ft.supportRichText = true;
            UIFactory.Stretch(ft.rectTransform, 106, 0, 20, 0);

            Footer(page, "CLOSE", Close, $"{InputMap.Prompt(GameAction.Loadout)} / [ESC] close");
        }

        /// <summary>Face editor: drawing pad on the left, live 3D preview of your head on the right.</summary>
        void BuildFace(RectTransform page)
        {
            Header(page, "DRAW YOUR FACE", "");

            const float pad = 560f, side = 260f, gap = 20f;
            float left = 30f, rightX = left + pad + gap;

            // pad: your colour behind the drawing, like the head it goes on
            var bg = UIFactory.Image("Pad", page, FacePreview.SkinColor);
            UIFactory.Place(bg.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(left, -130), new Vector2(pad, pad));
            var bo = bg.gameObject.AddComponent<Outline>();
            bo.effectColor = new Color(1, 1, 1, 0.25f);
            bo.effectDistance = new Vector2(2, -2);
            // faint centre guides
            var vGuide = UIFactory.Image("GuideV", bg.rectTransform, new Color(0, 0, 0, 0.12f));
            vGuide.raycastTarget = false;
            UIFactory.Place(vGuide.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(2, pad));
            var hGuide = UIFactory.Image("GuideH", bg.rectTransform, new Color(0, 0, 0, 0.12f));
            hGuide.raycastTarget = false;
            UIFactory.Place(hGuide.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(pad, 2));

            var drawing = UIFactory.Rect("Drawing", bg.rectTransform).gameObject.AddComponent<RawImage>();
            drawing.texture = FacePreview.Painter.Texture;
            UIFactory.Stretch(drawing.rectTransform);
            var canvas = drawing.gameObject.AddComponent<FaceCanvas>();

            // preview
            float previewH = side * FacePreview.TexHeight / FacePreview.TexWidth;
            var frame = UIFactory.Image("PreviewFrame", page, new Color(0.11f, 0.12f, 0.15f, 1f));
            UIFactory.Place(frame.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, -130), new Vector2(side, previewH));
            var tex = FacePreview.Get();
            if (tex != null)
            {
                var img = UIFactory.Rect("Preview", frame.rectTransform).gameObject.AddComponent<RawImage>();
                img.texture = tex;
                img.raycastTarget = false;
                UIFactory.Stretch(img.rectTransform);
            }
            else
            {
                var none = UIFactory.Text("NoPreview", frame.rectTransform, "NO PREVIEW", 18, TextAnchor.MiddleCenter, UIFactory.Dim, FontStyle.Bold);
                UIFactory.Stretch(none.rectTransform);
            }

            // ink meter
            float y = -130 - previewH - 16;
            var inkText = UIFactory.Text("Ink", page, "", 18, TextAnchor.MiddleLeft, UIFactory.Dim, FontStyle.Bold);
            UIFactory.Place(inkText.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, y), new Vector2(side, 24));
            var barBg = UIFactory.Image("InkBar", page, new Color(0.11f, 0.12f, 0.15f, 1f));
            UIFactory.Place(barBg.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, y - 28), new Vector2(side, 10));
            var bar = UIFactory.Image("Fill", barBg.rectTransform, UIFactory.Accent);
            bar.rectTransform.anchorMin = Vector2.zero;
            bar.rectTransform.pivot = new Vector2(0, 0.5f);
            bar.rectTransform.offsetMin = bar.rectTransform.offsetMax = Vector2.zero;
            void UpdateInk()
            {
                float used = canvas.InkUsed;
                inkText.text = $"INK  {Mathf.RoundToInt(used * 100)}%";
                inkText.color = used >= 0.999f ? UIFactory.Accent : UIFactory.Dim;
                bar.rectTransform.anchorMax = new Vector2(used, 1);
            }
            canvas.InkChanged += UpdateInk;
            UpdateInk();

            // undo / clear
            var undo = UIFactory.Button("Undo", page, "UNDO", canvas.Undo, 22);
            UIFactory.Place((RectTransform)undo.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, y - 58), new Vector2(side, 50));
            var clear = UIFactory.Button("Clear", page, "CLEAR", canvas.Clear, 22);
            UIFactory.Place((RectTransform)clear.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(rightX, y - 118), new Vector2(side, 50));

            Footer(page, "BACK", ShowList, "");
        }

        void BuildEdit(RectTransform page, int index)
        {
            var l = Loadouts.Get(index);
            Header(page, "EDIT LOADOUT " + (index + 1), NextSpawnHint());

            var body = UIFactory.Rect("Body", page);
            UIFactory.Place(body, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -130), new Vector2(WindowSize.x - 60, WindowSize.y - 240));
            UIFactory.VList(body, 10);

            // name
            UIFactory.Size(UIFactory.Text("NameLabel", body, "NAME", 18, TextAnchor.LowerLeft, UIFactory.Dim, FontStyle.Bold), 24);
            var field = UIFactory.Input(body, "Loadout name...", l.name, 16, v => Loadouts.Rename(index, v));
            UIFactory.Size(field, 52);

            // one box per loadout slot showing the equipped weapon; click it to choose another
            foreach (var slot in LoadoutSchema.Slots)
            {
                var s = slot.slot;
                UIFactory.Size(UIFactory.Text(slot.title, body, slot.title, 18, TextAnchor.LowerLeft, UIFactory.Dim, FontStyle.Bold), 34);

                var id = l.Get(s);
                bool empty = id == WeaponId.None;
                var box = UIFactory.Button(slot.title + "Box", body, "", () => ShowWeaponPicker(index, s));
                UIFactory.Size(box, WeaponCardHeight);
                var br = (RectTransform)box.transform;
                // preview on the left, name + hint on the right
                var area = UIFactory.Rect("PreviewArea", br);
                area.anchorMin = new Vector2(0, 0);
                area.anchorMax = new Vector2(0.5f, 1);
                area.offsetMin = new Vector2(12, 10);
                area.offsetMax = new Vector2(-6, -10);
                if (!empty) AddPreview(area, id);
                string label = empty ? "NONE" : WeaponCatalog.Get(id).displayName.ToUpperInvariant();
                var n = UIFactory.Text("Name", br, label, 34, TextAnchor.MiddleLeft, empty ? UIFactory.Dim : UIFactory.Accent, FontStyle.Bold);
                n.rectTransform.anchorMin = new Vector2(0.5f, 0.45f);
                n.rectTransform.anchorMax = new Vector2(1, 1);
                n.rectTransform.offsetMin = new Vector2(16, 0);
                n.rectTransform.offsetMax = new Vector2(-16, -10);
                var hint = UIFactory.Text("Hint", br, "CLICK TO CHANGE", 16, TextAnchor.UpperLeft, UIFactory.Dim, FontStyle.Bold);
                hint.rectTransform.anchorMin = new Vector2(0.5f, 0);
                hint.rectTransform.anchorMax = new Vector2(1, 0.45f);
                hint.rectTransform.offsetMin = new Vector2(16, 10);
                hint.rectTransform.offsetMax = new Vector2(-16, -6);
            }

            // perks: three boxes, click one to choose what goes in it (the same perk can be picked more than once)
            UIFactory.Size(UIFactory.Text("PerksLabel", body, $"PERKS  <color=#9aa0aa><size=15>up to {Perks.Slots} · picking a perk again stacks it</size></color>",
                18, TextAnchor.LowerLeft, UIFactory.Dim, FontStyle.Bold), 34).GetComponent<Text>().supportRichText = true;
            var boxes = UIFactory.Rect("PerkSlots", body);
            UIFactory.Size(boxes, PerkBoxHeight);
            var bh = boxes.gameObject.AddComponent<HorizontalLayoutGroup>();
            bh.spacing = 12;
            bh.childControlWidth = bh.childControlHeight = true;
            bh.childForceExpandWidth = bh.childForceExpandHeight = true;
            for (int i = 0; i < Perks.Slots; i++)
            {
                int slotIdx = i;
                var perk = Perks.Get(l.GetPerk(i));
                var box = UIFactory.Button("Perk" + (i + 1), boxes, "", () => ShowPerkPicker(index, slotIdx));
                var br = (RectTransform)box.transform;
                if (perk == null)
                {
                    ((Image)box.targetGraphic).color = new Color(0.11f, 0.12f, 0.15f, 1f);
                    var o = box.gameObject.AddComponent<Outline>();
                    o.effectColor = new Color(1, 1, 1, 0.12f);
                    o.effectDistance = new Vector2(2, -2);
                    var plus = UIFactory.Text("Empty", br, "+\n<size=16>EMPTY PERK SLOT</size>", 40, TextAnchor.MiddleCenter, UIFactory.Dim, FontStyle.Bold);
                    plus.supportRichText = true;
                    UIFactory.Stretch(plus.rectTransform);
                    continue;
                }
                PerkCardContents(br, perk, 24, 15);
                int stacks = 0;
                for (int k = 0; k < Perks.Slots; k++) if (l.GetPerk(k) == perk.id) stacks++;
                if (stacks > 1)
                {
                    var tag = UIFactory.Text("Stack", br, "x" + stacks, 18, TextAnchor.UpperRight, perk.color, FontStyle.Bold);
                    UIFactory.Stretch(tag.rectTransform, 0, 0, 10, 8);
                }
            }

            Footer(page, "BACK", ShowList, "[ESC] back");
        }

        /// <summary>Spinning render of a gun, fitted inside the given area.</summary>
        static void AddPreview(RectTransform area, WeaponId id)
        {
            var tex = WeaponPreview.Get(id);
            if (tex == null) return;
            var img = UIFactory.Rect("Preview", area).gameObject.AddComponent<RawImage>();
            img.texture = tex;
            img.raycastTarget = false;
            var fit = img.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = tex.width / (float)tex.height;
        }

        /// <summary>Modal grid of every weapon the slot allows, over the edit page.</summary>
        void BuildWeaponPicker(RectTransform page, int index, LoadoutSlot slot)
        {
            var l = Loadouts.Get(index);
            var options = LoadoutSchema.Options(slot);
            bool optional = LoadoutSchema.Slots[(int)slot].optional;
            int cardCount = options.Count + (optional ? 1 : 0);
            const int cols = 3;
            const float cardH = 200f, gap = 12f;
            int rows = Mathf.Max(1, (cardCount + cols - 1) / cols);
            float gridW = WindowSize.x - 130;
            float gridH = rows * cardH + (rows - 1) * gap;

            var dim = UIFactory.Image("WeaponPicker", page, new Color(0, 0, 0, 0.75f)); // blocks clicks to the page below
            UIFactory.Stretch(dim.rectTransform);
            var panel = UIFactory.Image("Panel", dim.rectTransform, new Color(0.08f, 0.09f, 0.11f, 1f)).rectTransform;
            UIFactory.Place(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(WindowSize.x - 80, Mathf.Min(WindowSize.y - 40, gridH + 170)));
            var o = panel.gameObject.AddComponent<Outline>();
            o.effectColor = UIFactory.Accent;
            o.effectDistance = new Vector2(2, -2);

            var title = UIFactory.Text("Title", panel, LoadoutSchema.Slots[(int)slot].title, 32, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(26, -16), new Vector2(600, 44));

            var grid = UIFactory.Rect("Options", panel);
            UIFactory.Place(grid, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -74), new Vector2(gridW, gridH));
            var g = grid.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = new Vector2((gridW - gap * (cols - 1)) / cols, cardH);
            g.spacing = new Vector2(gap, gap);
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = cols;
            g.childAlignment = TextAnchor.UpperCenter;

            foreach (var w in options)
            {
                var weapon = w;
                bool chosen = l.Get(slot) == w.id;
                var card = UIFactory.Button(w.displayName, grid, "", () => { Loadouts.SetItem(index, slot, weapon.id); ShowEdit(index); });
                var cr = (RectTransform)card.transform;
                ((Image)card.targetGraphic).color = chosen ? ChosenCardCol : UIFactory.ButtonCol;
                if (chosen)
                {
                    var co = card.gameObject.AddComponent<Outline>();
                    co.effectColor = UIFactory.Accent;
                    co.effectDistance = new Vector2(3, -3);
                }
                // name: top, shrinks to fit the card
                var n = UIFactory.Text("Name", cr, w.displayName.ToUpperInvariant(), 22, TextAnchor.MiddleCenter, chosen ? UIFactory.Accent : UIFactory.TextCol, FontStyle.Bold);
                n.horizontalOverflow = HorizontalWrapMode.Wrap;
                n.verticalOverflow = VerticalWrapMode.Truncate;
                n.resizeTextForBestFit = true;
                n.resizeTextMinSize = 12;
                n.resizeTextMaxSize = 22;
                n.rectTransform.anchorMin = new Vector2(0, 1);
                n.rectTransform.anchorMax = new Vector2(1, 1);
                n.rectTransform.pivot = new Vector2(0.5f, 1);
                n.rectTransform.offsetMin = new Vector2(10, -38);
                n.rectTransform.offsetMax = new Vector2(-10, -8);
                AddPreview(UIFactory.Stretch(UIFactory.Rect("PreviewArea", cr), 10, 8, 10, 42), w.id);
            }

            // optional slots can be left empty
            if (optional)
            {
                bool chosen = l.Get(slot) == WeaponId.None;
                var none = UIFactory.Button("None", grid, "NONE", () => { Loadouts.SetItem(index, slot, WeaponId.None); ShowEdit(index); }, 22);
                ((Image)none.targetGraphic).color = chosen ? ChosenCardCol : UIFactory.ButtonCol;
                UIFactory.Label(none).color = chosen ? UIFactory.Accent : UIFactory.Dim;
                if (chosen)
                {
                    var co = none.gameObject.AddComponent<Outline>();
                    co.effectColor = UIFactory.Accent;
                    co.effectDistance = new Vector2(3, -3);
                }
            }

            var cancel = UIFactory.Button("Cancel", panel, "CANCEL", () => ShowEdit(index), 22);
            UIFactory.Place((RectTransform)cancel.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 20), new Vector2(170, 46));
            var hint = UIFactory.Text("Hint", panel, "[ESC] cancel", 18, TextAnchor.MiddleLeft, UIFactory.Dim);
            UIFactory.Place(hint.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(26, 30), new Vector2(300, 30));
        }

        /// <summary>Colour stripe, name and description of a perk inside a card/button.</summary>
        static void PerkCardContents(RectTransform card, PerkInfo perk, int nameSize, int descSize)
        {
            var stripe = UIFactory.Image("Stripe", card, perk.color);
            stripe.raycastTarget = false;
            stripe.rectTransform.anchorMin = new Vector2(0, 0);
            stripe.rectTransform.anchorMax = new Vector2(0, 1);
            stripe.rectTransform.pivot = new Vector2(0, 0.5f);
            stripe.rectTransform.sizeDelta = new Vector2(6, 0);
            stripe.rectTransform.anchoredPosition = Vector2.zero;

            var n = UIFactory.Text("Name", card, perk.displayName.ToUpperInvariant(), nameSize, TextAnchor.UpperLeft, perk.color, FontStyle.Bold);
            n.rectTransform.anchorMin = new Vector2(0, 1);
            n.rectTransform.anchorMax = new Vector2(1, 1);
            n.rectTransform.pivot = new Vector2(0.5f, 1);
            n.rectTransform.offsetMin = new Vector2(18, -(nameSize + 16));
            n.rectTransform.offsetMax = new Vector2(-12, -10);

            var d = UIFactory.Text("Desc", card, perk.description, descSize, TextAnchor.UpperLeft, UIFactory.TextCol);
            d.horizontalOverflow = HorizontalWrapMode.Wrap;
            d.verticalOverflow = VerticalWrapMode.Truncate;
            UIFactory.Stretch(d.rectTransform, 18, 8, 12, nameSize + 18);
        }

        /// <summary>Modal list of every perk (plus "empty") over the edit page.</summary>
        void BuildPerkPicker(RectTransform page, int index, int slot)
        {
            var l = Loadouts.Get(index);
            var current = l.GetPerk(slot);

            var dim = UIFactory.Image("PerkPicker", page, new Color(0, 0, 0, 0.75f)); // blocks clicks to the page below
            UIFactory.Stretch(dim.rectTransform);
            var panel = UIFactory.Image("Panel", dim.rectTransform, new Color(0.08f, 0.09f, 0.11f, 1f)).rectTransform;
            // two-column grid (every perk + "empty")
            const int cols = 2;
            float rowH = 110f, gap = 10f, listW = WindowSize.x - 130;
            int rows = (Perks.All.Length + 1 + cols - 1) / cols;
            float listH = rows * rowH + (rows - 1) * gap;
            UIFactory.Place(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(WindowSize.x - 80, listH + 170));
            var o = panel.gameObject.AddComponent<Outline>();
            o.effectColor = UIFactory.Accent;
            o.effectDistance = new Vector2(2, -2);

            var title = UIFactory.Text("Title", panel, $"PERK SLOT {slot + 1}", 32, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(title.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(26, -16), new Vector2(600, 44));

            var list = UIFactory.Rect("Options", panel);
            UIFactory.Place(list, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -74), new Vector2(listW, listH));
            var grid = list.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2((listW - gap * (cols - 1)) / cols, rowH);
            grid.spacing = new Vector2(gap, gap);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = cols;

            foreach (var p in Perks.All)
            {
                var perk = p;
                bool chosen = current == perk.id;
                var b = UIFactory.Button(perk.displayName, list, "", () => { Loadouts.SetPerk(index, slot, perk.id); ShowEdit(index); });
                ((Image)b.targetGraphic).color = chosen ? ChosenCardCol : UIFactory.ButtonCol;
                if (chosen)
                {
                    var co = b.gameObject.AddComponent<Outline>();
                    co.effectColor = UIFactory.Accent;
                    co.effectDistance = new Vector2(3, -3);
                }
                PerkCardContents((RectTransform)b.transform, perk, 22, 16);
                if (!perk.stackable)
                {
                    int other = -1;
                    for (int k = 0; k < Perks.Slots; k++) if (k != slot && l.GetPerk(k) == perk.id) other = k;
                    string note = other >= 0 ? $"MOVES FROM SLOT {other + 1}" : "DOESN'T STACK";
                    var tag = UIFactory.Text("NoStack", b.transform, note, 15, TextAnchor.UpperRight, UIFactory.Dim, FontStyle.Bold);
                    UIFactory.Stretch(tag.rectTransform, 0, 0, 12, 12);
                }
            }
            var none = UIFactory.Button("None", list, "EMPTY", () => { Loadouts.SetPerk(index, slot, PerkId.None); ShowEdit(index); }, 22);
            UIFactory.Label(none).color = current == PerkId.None ? UIFactory.Accent : UIFactory.Dim;

            var cancel = UIFactory.Button("Cancel", panel, "CANCEL", () => ShowEdit(index), 22);
            UIFactory.Place((RectTransform)cancel.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-24, 20), new Vector2(170, 46));
            var hint = UIFactory.Text("Hint", panel, "[ESC] cancel", 18, TextAnchor.MiddleLeft, UIFactory.Dim);
            UIFactory.Place(hint.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(26, 30), new Vector2(300, 30));
        }

        static string NextSpawnHint()
        {
            var pc = PlayerController.Local;
            if (pc == null) return "Used when you join a game";
            return pc.IsAlive ? "Changes apply when you next spawn" : "Applied when you respawn";
        }

        static void Header(RectTransform page, string title, string sub)
        {
            var t = UIFactory.Text("Title", page, title, 40, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            UIFactory.Place(t.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -20), new Vector2(700, 50));
            var s = UIFactory.Text("Sub", page, sub, 20, TextAnchor.MiddleLeft, UIFactory.Dim);
            UIFactory.Place(s.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(30, -72), new Vector2(700, 30));
        }

        static void Footer(RectTransform page, string button, UnityEngine.Events.UnityAction onClick, string hint)
        {
            var b = UIFactory.Button("Footer", page, button, onClick, 26);
            UIFactory.Place((RectTransform)b.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-30, 25), new Vector2(200, 54));
            var h = UIFactory.Text("Hint", page, hint, 18, TextAnchor.MiddleLeft, UIFactory.Dim);
            UIFactory.Place(h.rectTransform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(30, 40), new Vector2(500, 30));
        }
    }
}
