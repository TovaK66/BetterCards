using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.Modal;
using Nosebleed.Pancake.Models;
using Nosebleed.Pancake.View;
using UObject = UnityEngine.Object;
using UnityEngine;
using UnityEngine.UI;

namespace BetterCards;

[BepInPlugin("com.tovak.vc.bettercards", "BetterCards", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static new BepInEx.Logging.ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        ClassInjector.RegisterTypeInIl2Cpp<ComboObserver>();
        var go = new GameObject("BetterCards");
        GameObject.DontDestroyOnLoad(go);
        go.AddComponent<ComboObserver>();
        Log.LogInfo("BetterCards chargé !");
    }
}

public class ComboObserver : MonoBehaviour
{
    public ComboObserver(IntPtr ptr) : base(ptr) { }

    private bool _wasOpen = false;

    public void Awake() { }

    public void Update()
    {
        var modal = UObject.FindObjectOfType<ChooseCardModal>();
        bool isOpen = modal != null && modal.IsOpen;
        if (isOpen && !_wasOpen) OnModalOpened(modal);
        _wasOpen = isOpen;
    }

    void OnModalOpened(ChooseCardModal modal)
    {
        Plugin.Log.LogInfo("[CI] modal opened");
        try
        {
            var playerModel = modal._playerModel;
            if (playerModel == null) return;

            var views = modal._cardChoiceViews;
            if (views == null) return;

            var offeredNames = new HashSet<string>();
            foreach (var view in views)
                if (view?.CardConfig?.name != null) offeredNames.Add(view.CardConfig.name);

            var ownedNormNames = new HashSet<string>();
            var countByBase = new Dictionary<string, int>();
            var ownedConfigs = new List<CardConfig>();
            var allCards = playerModel._allCards;
            if (allCards != null)
                foreach (var cm in allCards)
                {
                    var cfg = cm?.CardConfig;
                    if (cfg == null) continue;
                    if (cfg.name != null)
                    {
                        var bn = GetBaseKey(cfg.name);
                        ownedNormNames.Add(bn);
                        countByBase[bn] = countByBase.TryGetValue(bn, out var cnt) ? cnt + 1 : 1;
                    }
                    if (!offeredNames.Contains(cfg.name)) ownedConfigs.Add(cfg);
                }

            foreach (var view in views)
            {
                if (view == null) continue;
                var tc = GetTweenContainer(view.gameObject);
                var old = tc.Find(BadgeName); if (old != null) UObject.DestroyImmediate(old.gameObject);
                var oldC = tc.Find(CountBadgeName); if (oldC != null) UObject.DestroyImmediate(oldC.gameObject);
            }

            foreach (var view in views)
            {
                if (view == null) continue;
                var cfg = view.CardConfig;
                if (cfg == null) continue;

                var bn2 = cfg.name != null ? GetBaseKey(cfg.name) : "";
                int owned = bn2.Length > 0 && countByBase.TryGetValue(bn2, out var oc) ? oc : 0;
                AddCountBadge(view.gameObject, owned);

                if (cfg.name == null || ownedNormNames.Contains(bn2)) continue;
                var (hasCombo, evoName, _) = CheckCombo(cfg, ownedConfigs);
                if (hasCombo)
                {
                    Plugin.Log.LogInfo($"[CI] EVO disponible : '{cfg.Name}' → '{evoName}'");
                    AddBadge(view.gameObject, evoName);
                }
            }
        }
        catch (Exception ex) { Plugin.Log.LogError($"[ComboIndicator] {ex}"); }
    }

    static string[] ParseComponents(string desc)
    {
        if (string.IsNullOrEmpty(desc)) return Array.Empty<string>();
        var colon = desc.IndexOf(':');
        if (colon < 0) return Array.Empty<string>();
        return desc.Substring(colon + 1)
                   .Replace(".", "")
                   .Split(',')
                   .Select(s => s.Trim())
                   .Where(s => s.Length > 0)
                   .ToArray();
    }

    static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

    // "Card_A_0_EmptyTome" → "emptytome"  (pour matching combo : nom seul)
    static string GetBaseName(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return "";
        var parts = assetName.Split('_');
        return parts.Length >= 4 ? Norm(string.Join("", parts.Skip(3))) : Norm(assetName);
    }

    // "Card_A_0_Armor" → "a_0_armor", "Card_D_1_Armor" → "d_1_armor"  (pour ownership : type + niveau inclus)
    static string GetBaseKey(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return "";
        var parts = assetName.Split('_');
        if (parts.Length >= 4)
            return parts[1].ToLowerInvariant() + "_" + parts[2] + "_" + Norm(string.Join("", parts.Skip(3)));
        return Norm(assetName);
    }

    // Égalité exacte sur le nom de base : "tome" ne matche plus "emptytome"
    static bool CardMatchesComp(CardConfig card, string compNorm)
    {
        if (compNorm.Length == 0) return false;
        if (card.name != null && GetBaseName(card.name) == compNorm) return true;
        if (card.Name != null && Norm(card.Name) == compNorm) return true;
        return false;
    }

    static bool OwnsComp(string compName, List<CardConfig> owned)
    {
        var needle = Norm(compName);
        if (needle.Length == 0) return false;
        foreach (var c in owned)
            if (c != null && CardMatchesComp(c, needle)) return true;
        return false;
    }

    static (bool, string, Sprite) CheckCombo(CardConfig offered, List<CardConfig> ownedConfigs)
    {
        // Case 1 : la carte offerte peut évoluer si le joueur possède tous les autres composants
        if (offered.HasEvolution)
        {
            var comps = ParseComponents(offered.GetEvolveRequirementDescription());
            if (comps.Length > 0)
            {
                bool allSatisfied = comps.All(c =>
                    CardMatchesComp(offered, Norm(c)) || OwnsComp(c, ownedConfigs));
                // Au moins un composant doit venir du deck (évite les faux positifs sur description incomplète)
                bool anyFromDeck = comps.Any(c =>
                    !CardMatchesComp(offered, Norm(c)) && OwnsComp(c, ownedConfigs));
                if (allSatisfied && anyFromDeck)
                {
                    var evolved = offered.EvolvedCardConfig;
                    return (true, evolved?.Name ?? "?", FirstSprite(evolved));
                }
            }
        }

        // Case 2 : la carte offerte complète le combo d'une carte déjà possédée
        foreach (var owned in ownedConfigs)
        {
            if (owned == null || !owned.HasEvolution) continue;
            var comps = ParseComponents(owned.GetEvolveRequirementDescription());
            if (comps.Length == 0) continue;

            bool offeredIsComp = comps.Any(c => {
                var cn = Norm(c);
                return cn.Length > 0 && CardMatchesComp(offered, cn);
            });
            if (!offeredIsComp) continue;

            // Tous les composants doivent être satisfaits par la carte offerte ou le deck
            bool allOtherOwned = comps.All(c => {
                var cn = Norm(c);
                if (cn.Length == 0) return true;
                if (CardMatchesComp(offered, cn)) return true;
                return OwnsComp(c, ownedConfigs);
            });

            if (allOtherOwned)
            {
                var evolved = owned.EvolvedCardConfig;
                return (true, evolved?.Name ?? "?", FirstSprite(evolved));
            }
        }

        return (false, "", null);
    }

    static Sprite FirstSprite(CardConfig cfg)
    {
        if (cfg == null) return null;
        var arr = cfg.sprites;
        if (arr == null || arr.Length == 0) return null;
        return arr[0];
    }

    const string BadgeName = "VCComboBadge";
    static Sprite _badgeSprite;

    static Sprite GetBadgeSprite()
    {
        if (_badgeSprite != null) return _badgeSprite;
        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        float cx = sz / 2f, cy = sz / 2f;
        var pixels = new Color[sz * sz];

        // Background circle + gold ring with "liseré brillant"
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            Color col = Color.clear;

            if (dist < 32f)
            {
                float angle = Mathf.Atan2(dy, dx);
                // Directional light: top-left appears brighter (classic metallic sheen)
                float dirLight = (Mathf.Cos(angle + Mathf.PI * 0.3f) + 1f) * 0.5f;

                if (dist >= 29f) // outer ambient glow
                {
                    float t = 1f - (dist - 29f) / 3f;
                    col = new Color(0.6f, 0.18f, 0f, t * t * 0.55f);
                }
                else if (dist >= 19.5f) // gold ring with concentric "liseré" bands
                {
                    float ringPos = (dist - 19.5f) / 9.5f; // 0=inner edge, 1=outer edge
                    // Concentric stripes create the engraved-metal look
                    float band = (Mathf.Sin(ringPos * Mathf.PI * 3.5f - 0.3f) + 1f) * 0.5f;
                    // Brighter at inner and outer edges
                    float edgeBoost = Mathf.Pow(Mathf.Abs(ringPos * 2f - 1f), 1.5f);
                    float brightness = band * 0.32f + dirLight * 0.33f + edgeBoost * 0.28f + 0.07f;
                    brightness = Mathf.Clamp01(brightness);
                    col = new Color(
                        Mathf.Lerp(0.48f, 1.0f, brightness),
                        Mathf.Lerp(0.20f, 0.84f, brightness),
                        0f, 1f
                    );
                }
                else if (dist >= 17.5f) // inner shadow at ring/background border
                {
                    float t = (dist - 17.5f) / 2f;
                    col = new Color(0.08f, 0.03f, 0f, t * 0.9f);
                }
                else // dark interior
                {
                    col = new Color(0.04f, 0.012f, 0f, 0.97f);
                }
            }
            pixels[y * sz + x] = col;
        }

        // EVO pixel-art glyphs — 5×7, scale=3 → 15×21 per letter
        // row[0] = top of letter; Y-flipped when drawing into texture
        const int scale = 3, gw = 5, gh = 7;
        byte[][][] glyphs = {
            // E
            new byte[][]{
                new byte[]{1,1,1,1,1},
                new byte[]{1,0,0,0,0},
                new byte[]{1,0,0,0,0},
                new byte[]{1,1,1,1,0},
                new byte[]{1,0,0,0,0},
                new byte[]{1,0,0,0,0},
                new byte[]{1,1,1,1,1},
            },
            // V
            new byte[][]{
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{0,1,0,1,0},
                new byte[]{0,1,0,1,0},
                new byte[]{0,0,1,0,0},
            },
            // O
            new byte[][]{
                new byte[]{0,1,1,1,0},
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1},
                new byte[]{0,1,1,1,0},
            },
        };

        int letterW = gw * scale, letterH = gh * scale, gap = scale;
        int totalW = 3 * letterW + 2 * gap;
        int xOff = (sz - totalW) / 2;
        int yOff = (sz - letterH) / 2;

        // Shadow pass (offset 1px right, 1px down on screen = 1px lower Y in texture)
        for (int g = 0; g < 3; g++)
        {
            int gxStart = xOff + g * (letterW + gap);
            for (int r = 0; r < gh; r++)
            for (int col = 0; col < gw; col++)
            if (glyphs[g][r][col] != 0)
            for (int sy = 0; sy < scale; sy++)
            for (int sx = 0; sx < scale; sx++)
            {
                int px = gxStart + col * scale + sx + 1;
                int py = yOff + (gh - 1 - r) * scale + sy - 1;
                if (px >= 0 && px < sz && py >= 0 && py < sz)
                    pixels[py * sz + px] = new Color(0f, 0f, 0f, 0.88f);
            }
        }

        // Color pass — gradient: red at bottom of letter, bright gold at top
        for (int g = 0; g < 3; g++)
        {
            int gxStart = xOff + g * (letterW + gap);
            for (int r = 0; r < gh; r++)
            for (int col = 0; col < gw; col++)
            if (glyphs[g][r][col] != 0)
            for (int sy = 0; sy < scale; sy++)
            for (int sx = 0; sx < scale; sx++)
            {
                int px = gxStart + col * scale + sx;
                int py = yOff + (gh - 1 - r) * scale + sy;
                if (px >= 0 && px < sz && py >= 0 && py < sz)
                {
                    // py: low = screen-bottom (red), high = screen-top (gold)
                    float gradT = (float)(py - yOff) / letterH;
                    pixels[py * sz + px] = new Color(
                        1f,
                        Mathf.Lerp(0.08f, 0.90f, gradT),
                        Mathf.Lerp(0f,    0.12f, gradT),
                        1f
                    );
                }
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        _badgeSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        return _badgeSprite;
    }

    // ─── Count / NEW badge ────────────────────────────────────────────────────

    const string CountBadgeName = "VCCountBadge";
    static Sprite _newBadgeSprite;
    static readonly Sprite[] _countBadgeCache = new Sprite[10];

    // 5×7 glyphs : N, E, W
    static readonly byte[][][] GlyphsNEW = {
        new byte[][]{ new byte[]{1,0,0,0,1}, new byte[]{1,1,0,0,1}, new byte[]{1,1,0,0,1},
                      new byte[]{1,0,1,0,1}, new byte[]{1,0,0,1,1}, new byte[]{1,0,0,1,1}, new byte[]{1,0,0,0,1} },
        new byte[][]{ new byte[]{1,1,1,1,1}, new byte[]{1,0,0,0,0}, new byte[]{1,0,0,0,0},
                      new byte[]{1,1,1,1,0}, new byte[]{1,0,0,0,0}, new byte[]{1,0,0,0,0}, new byte[]{1,1,1,1,1} },
        new byte[][]{ new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1},
                      new byte[]{1,0,1,0,1}, new byte[]{1,0,1,0,1}, new byte[]{0,1,0,1,0}, new byte[]{0,1,0,1,0} },
    };
    // 3×3 signe ×
    static readonly byte[][] GlyphTimes = {
        new byte[]{1,0,1}, new byte[]{0,1,0}, new byte[]{1,0,1},
    };
    // 3×5 chiffres 0–9
    static readonly byte[][][] GlyphsDigits = {
        new byte[][]{ new byte[]{0,1,0},new byte[]{1,0,1},new byte[]{1,0,1},new byte[]{1,0,1},new byte[]{0,1,0} },
        new byte[][]{ new byte[]{0,1,0},new byte[]{1,1,0},new byte[]{0,1,0},new byte[]{0,1,0},new byte[]{1,1,1} },
        new byte[][]{ new byte[]{1,1,0},new byte[]{0,0,1},new byte[]{0,1,0},new byte[]{1,0,0},new byte[]{1,1,1} },
        new byte[][]{ new byte[]{1,1,0},new byte[]{0,0,1},new byte[]{0,1,0},new byte[]{0,0,1},new byte[]{1,1,0} },
        new byte[][]{ new byte[]{1,0,1},new byte[]{1,0,1},new byte[]{1,1,1},new byte[]{0,0,1},new byte[]{0,0,1} },
        new byte[][]{ new byte[]{1,1,1},new byte[]{1,0,0},new byte[]{1,1,0},new byte[]{0,0,1},new byte[]{1,1,0} },
        new byte[][]{ new byte[]{0,1,1},new byte[]{1,0,0},new byte[]{1,1,0},new byte[]{1,0,1},new byte[]{0,1,0} },
        new byte[][]{ new byte[]{1,1,1},new byte[]{0,0,1},new byte[]{0,1,0},new byte[]{0,1,0},new byte[]{0,1,0} },
        new byte[][]{ new byte[]{0,1,0},new byte[]{1,0,1},new byte[]{0,1,0},new byte[]{1,0,1},new byte[]{0,1,0} },
        new byte[][]{ new byte[]{0,1,0},new byte[]{1,0,1},new byte[]{0,1,1},new byte[]{0,0,1},new byte[]{0,1,0} },
    };

    // Capsule SDF helper : dist ≤ 1 = inside pill
    static float PillDist(float x, float y, float cx, float cy, float halfLen, float r)
    {
        float dx = Mathf.Max(0f, Mathf.Abs(x - cx) - halfLen);
        float dy = y - cy;
        return Mathf.Sqrt(dx * dx + dy * dy) / r;
    }

    static Sprite GetNewBadgeSprite()
    {
        if (_newBadgeSprite != null) return _newBadgeSprite;
        const int tw = 56, th = 20, gw = 5, gh = 7, scale = 2, gap = 2;
        var tex = new Texture2D(tw, th, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[tw * th];
        float cx = tw / 2f, cy = th / 2f, r = th / 2f - 1f, halfLen = (tw - th) / 2f;

        for (int y = 0; y < th; y++)
        for (int x = 0; x < tw; x++)
        {
            float d = PillDist(x + .5f, y + .5f, cx, cy, halfLen, r);
            if (d <= 1f)
            {
                float hue = (float)x / tw;
                var c = Color.HSVToRGB(hue, 0.88f, 0.9f);
                float edge = Mathf.Clamp01((1f - d) / 0.15f);
                pixels[y * tw + x] = new Color(c.r, c.g, c.b, edge * 0.96f);
            }
        }

        int lw = gw * scale, lh = gh * scale;
        int xOff = (tw - (3 * lw + 2 * gap)) / 2, yOff = (th - lh) / 2;
        for (int pass = 0; pass < 2; pass++)
        for (int g = 0; g < 3; g++) {
            int gx = xOff + g * (lw + gap);
            for (int row = 0; row < gh; row++)
            for (int col = 0; col < gw; col++)
            if (GlyphsNEW[g][row][col] != 0)
            for (int sy = 0; sy < scale; sy++)
            for (int sx = 0; sx < scale; sx++) {
                int px = gx + col * scale + sx + (pass == 0 ? 1 : 0);
                int py = yOff + (gh - 1 - row) * scale + sy - (pass == 0 ? 1 : 0);
                if (px >= 0 && px < tw && py >= 0 && py < th)
                    pixels[py * tw + px] = pass == 0 ? new Color(0, 0, 0, 0.65f) : Color.white;
            }
        }
        tex.SetPixels(pixels); tex.Apply();
        _newBadgeSprite = Sprite.Create(tex, new Rect(0, 0, tw, th), new Vector2(0.5f, 0.5f), 100f);
        return _newBadgeSprite;
    }

    static Sprite GetCountBadgeSprite(int n)
    {
        int key = Mathf.Min(n, 9);
        if (_countBadgeCache[key] != null) return _countBadgeCache[key];
        const int tw = 30, th = 16, scale = 2;
        var tex = new Texture2D(tw, th, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[tw * th];
        float cx = tw / 2f, cy = th / 2f, r = th / 2f - 1f, halfLen = (tw - th) / 2f;

        for (int y = 0; y < th; y++)
        for (int x = 0; x < tw; x++) {
            float d = PillDist(x + .5f, y + .5f, cx, cy, halfLen, r);
            if (d <= 1f) {
                float edge = Mathf.Clamp01((1f - d) / 0.15f);
                pixels[y * tw + x] = new Color(0.05f, 0.03f, 0f, edge * 0.88f);
            }
        }

        // × (3×3 scale 2) + gap + chiffre (3×5 scale 2)
        int totalW = 3 * scale + scale + 3 * scale;
        int xStart = (tw - totalW) / 2;
        int yTimes = (th - 3 * scale) / 2, yDig = (th - 5 * scale) / 2;
        var gold = new Color(1f, 0.88f, 0.2f, 1f);

        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 3; col++)
        if (GlyphTimes[row][col] != 0)
        for (int sy = 0; sy < scale; sy++)
        for (int sx = 0; sx < scale; sx++) {
            int px = xStart + col * scale + sx, py = yTimes + (2 - row) * scale + sy;
            if (px >= 0 && px < tw && py >= 0 && py < th) pixels[py * tw + px] = gold;
        }
        var dg = GlyphsDigits[key];
        int xd = xStart + 3 * scale + scale;
        for (int row = 0; row < 5; row++)
        for (int col = 0; col < 3; col++)
        if (dg[row][col] != 0)
        for (int sy = 0; sy < scale; sy++)
        for (int sx = 0; sx < scale; sx++) {
            int px = xd + col * scale + sx, py = yDig + (4 - row) * scale + sy;
            if (px >= 0 && px < tw && py >= 0 && py < th) pixels[py * tw + px] = gold;
        }
        tex.SetPixels(pixels); tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, tw, th), new Vector2(0.5f, 0.5f), 100f);
        _countBadgeCache[key] = sprite;
        return sprite;
    }

    static Transform GetTweenContainer(GameObject go)
    {
        Transform t = go.transform;
        for (int i = 0; i < 4; i++)
            if (t.childCount > 0) t = t.GetChild(0);
        return t;
    }

    static void AddCountBadge(GameObject go, int count)
    {
        var tween = GetTweenContainer(go);
        if (tween.Find(CountBadgeName) != null) return;

        var bg = new GameObject(CountBadgeName);
        bg.layer = go.layer;
        bg.transform.SetParent(tween, false);
        var rt = bg.AddComponent<RectTransform>();
        rt.anchoredPosition = new Vector2(-52f, -17.5f); // haut-gauche, symétrique au EVO
        rt.sizeDelta = count == 0 ? new Vector2(46f, 16f) : new Vector2(28f, 14f);
        bg.transform.SetAsLastSibling();
        var img = bg.AddComponent<Image>();
        img.sprite = count == 0 ? GetNewBadgeSprite() : GetCountBadgeSprite(count);
    }

    // ─── EVO badge ────────────────────────────────────────────────────────────

    static void AddBadge(GameObject go, string evoName)
    {
        var tween = GetTweenContainer(go);
        if (tween.Find(BadgeName) != null) return;

        var badgeGo = new GameObject(BadgeName);
        badgeGo.layer = go.layer;
        badgeGo.transform.SetParent(tween, false);
        var badgeRT = badgeGo.AddComponent<RectTransform>();
        badgeRT.anchoredPosition = new Vector2(59f, -17.5f);
        badgeRT.sizeDelta = new Vector2(48f, 48f);
        badgeGo.transform.SetAsLastSibling();

        var img = badgeGo.AddComponent<Image>();
        img.sprite = GetBadgeSprite();
    }
}
