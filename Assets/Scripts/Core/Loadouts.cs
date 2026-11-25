using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gunbound
{
    /// <summary>Equipment slots in a loadout. Append new slots at the end (saves store them by index).</summary>
    public enum LoadoutSlot { Primary, Secondary }

    /// <summary>
    /// What each loadout slot is called and which weapons it may hold. The loadout menu is built from this,
    /// so adding a slot or restricting a slot's options only needs a change here.
    /// </summary>
    public static class LoadoutSchema
    {
        public class SlotInfo
        {
            public LoadoutSlot slot;
            public string title;
            /// <summary>What a fresh loadout has here (WeaponId.None for an optional slot = empty).</summary>
            public WeaponId defaultWeapon;
            /// <summary>The slot may be left empty (WeaponId.None).</summary>
            public bool optional;
            /// <summary>Which weapons may go in this slot.</summary>
            public Func<WeaponInfo, bool> allows = _ => true;
        }

        public static readonly SlotInfo[] Slots =
        {
            new() { slot = LoadoutSlot.Primary, title = "PRIMARY", defaultWeapon = WeaponId.Wolfgang },
            new() { slot = LoadoutSlot.Secondary, title = "SECONDARY", defaultWeapon = WeaponId.None, optional = true },
        };

        public static int SlotCount => Slots.Length;

        /// <summary>Can this slot hold this weapon (None only if the slot is optional)?</summary>
        public static bool Accepts(LoadoutSlot slot, int id)
        {
            var info = Slots[(int)slot];
            if (id == (int)WeaponId.None) return info.optional;
            return WeaponCatalog.IsValid(id) && info.allows(WeaponCatalog.Get((WeaponId)id));
        }

        public static List<WeaponInfo> Options(LoadoutSlot slot)
        {
            var info = Slots[(int)slot];
            var list = new List<WeaponInfo>();
            foreach (var w in WeaponCatalog.All) if (info.allows(w)) list.Add(w);
            return list;
        }
    }

    [Serializable]
    public class Loadout
    {
        public string name;
        /// <summary>WeaponId per LoadoutSlot (index = (int)LoadoutSlot).</summary>
        public int[] items;
        /// <summary>PerkId per perk slot (0 = empty). The same perk may appear more than once (stacks).</summary>
        public int[] perks;

        public PerkId GetPerk(int slot) => perks != null && slot >= 0 && slot < perks.Length && Perks.IsValid(perks[slot]) ? (PerkId)perks[slot] : PerkId.None;

        public void SetPerk(int slot, PerkId p)
        {
            Fix(this, 0);
            if (slot < 0 || slot >= perks.Length) return;
            // a non-stacking perk moves here from any other slot
            var info = Perks.Get(p);
            if (info != null && !info.stackable)
                for (int i = 0; i < perks.Length; i++) if (perks[i] == (int)p) perks[i] = 0;
            perks[slot] = (int)p;
        }

        public WeaponId Get(LoadoutSlot s)
        {
            int i = (int)s;
            return items != null && i < items.Length && LoadoutSchema.Accepts(s, items[i]) ? (WeaponId)items[i] : LoadoutSchema.Slots[i].defaultWeapon;
        }

        public void Set(LoadoutSlot s, WeaponId w)
        {
            Fix(this, 0);
            if (!LoadoutSchema.Accepts(s, (int)w)) return;
            items[(int)s] = (int)w;
        }

        /// <summary>Make sure every slot exists and holds a weapon that slot allows.</summary>
        public static Loadout Fix(Loadout l, int index)
        {
            l ??= new Loadout();
            if (string.IsNullOrWhiteSpace(l.name)) l.name = "LOADOUT " + (index + 1);
            int n = LoadoutSchema.SlotCount;
            if (l.items == null || l.items.Length != n)
            {
                var old = l.items ?? Array.Empty<int>();
                l.items = new int[n];
                for (int i = 0; i < n; i++) l.items[i] = i < old.Length ? old[i] : -1;
            }
            for (int i = 0; i < n; i++)
                if (!LoadoutSchema.Accepts((LoadoutSlot)i, l.items[i]))
                    l.items[i] = (int)LoadoutSchema.Slots[i].defaultWeapon;
            if (l.perks == null || l.perks.Length != Perks.Slots)
            {
                var old = l.perks ?? Array.Empty<int>();
                l.perks = new int[Perks.Slots];
                for (int i = 0; i < Perks.Slots; i++) l.perks[i] = i < old.Length ? old[i] : 0;
            }
            for (int i = 0; i < Perks.Slots; i++)
            {
                if (!Perks.IsValid(l.perks[i])) { l.perks[i] = 0; continue; }
                // non-stacking perks: keep only the first copy
                if (Perks.Get((PerkId)l.perks[i]).stackable) continue;
                for (int k = 0; k < i; k++)
                    if (l.perks[k] == l.perks[i]) { l.perks[i] = 0; break; }
            }
            return l;
        }
    }

    /// <summary>The player's saved loadouts (PlayerPrefs, JSON) and which one is selected.</summary>
    public static class Loadouts
    {
        public const int Count = 5;
        const string PrefKey = "gb_loadouts";

        [Serializable]
        class SaveData
        {
            public int selected;
            public Loadout[] loadouts;
        }

        static SaveData _data;
        public static event Action Changed;

        static SaveData Data
        {
            get
            {
                if (_data == null) Load();
                return _data;
            }
        }

        public static int SelectedIndex => Data.selected;
        public static Loadout Selected => Data.loadouts[Data.selected];
        public static Loadout Get(int i) => Data.loadouts[Mathf.Clamp(i, 0, Count - 1)];

        public static void Select(int i)
        {
            Data.selected = Mathf.Clamp(i, 0, Count - 1);
            Save();
        }

        public static void SetItem(int loadout, LoadoutSlot slot, WeaponId w)
        {
            Get(loadout).Set(slot, w);
            Save();
        }

        public static void SetPerk(int loadout, int slot, PerkId p)
        {
            Get(loadout).SetPerk(slot, p);
            Save();
        }

        public static void Rename(int loadout, string name)
        {
            var l = Get(loadout);
            l.name = GameSettings.SanitizeName(name);
            Loadout.Fix(l, loadout);
            Save();
        }

        static void Load()
        {
            SaveData d = null;
            string json = PlayerPrefs.GetString(PrefKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { d = JsonUtility.FromJson<SaveData>(json); } catch { d = null; }
            }
            d ??= new SaveData();
            var arr = new Loadout[Count];
            for (int i = 0; i < Count; i++)
                arr[i] = Loadout.Fix(d.loadouts != null && i < d.loadouts.Length ? d.loadouts[i] : null, i);
            d.loadouts = arr;
            d.selected = Mathf.Clamp(d.selected, 0, Count - 1);
            _data = d;
        }

        static void Save()
        {
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(Data));
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}
