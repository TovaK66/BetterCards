using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.Modal;
using Nosebleed.Pancake.Models;
using Nosebleed.Pancake.View;
using TMPro;
using UObject = UnityEngine.Object;
using UnityEngine;
using UnityEngine.UI;

namespace BetterCards;

[BepInPlugin("com.tovak.vc.bettercards", "BetterCards", "1.2.1")]
public class Plugin : BasePlugin
{
    internal static new BepInEx.Logging.ManualLogSource Log;
    internal static string CurrentLangCode = "";
    internal static bool LocaleRebuildPending = false;

    public override void Load()
    {
        Log = base.Log;
        ClassInjector.RegisterTypeInIl2Cpp<ComboObserver>();
        var go = new GameObject("BetterCards");
        GameObject.DontDestroyOnLoad(go);
        go.AddComponent<ComboObserver>();

        // Lecture initiale du locale
        try
        {
            var loc = LocalizationSettings.SelectedLocale;
            if (loc != null) CurrentLangCode = (loc.Identifier.Code ?? "").ToLowerInvariant();
        }
        catch { }

        // Patch Harmony : capture les changements de langue en temps réel
        try
        {
            var harmony = new HarmonyLib.Harmony("com.tovak.vc.bettercards");
            harmony.PatchAll(typeof(LocalePatch));
        }
        catch (Exception ex) { Log.LogWarning($"[BetterCards] harmony error: {ex.Message}"); }

        Log.LogInfo("BetterCards chargé !");
    }
}

[HarmonyLib.HarmonyPatch(typeof(LocalizationSettings), "SetSelectedLocale")]
public static class LocalePatch
{
    public static void Postfix(UnityEngine.Localization.Locale locale)
    {
        try
        {
            string code = "";
            if (locale != null) code = (locale.Identifier.Code ?? "").ToLowerInvariant();
            if (code != Plugin.CurrentLangCode)
            {
                Plugin.CurrentLangCode = code;
                Plugin.LocaleRebuildPending = true;
            }
        }
        catch { }
    }
}

public class ComboObserver : MonoBehaviour
{
    public ComboObserver(IntPtr ptr) : base(ptr) { }

    private bool _wasOpen = false;
    private string _lastCardSignature = "";
    private ChooseCardModal _cachedModal;
    private int _searchCooldown = 0;

    private DeckBoxModal _cachedDeckModal;
    private int _deckSearchCooldown = 0;
    private int _deckAliveCheck = 0;
    private bool _wasDeckOpen = false;
    private PlayerModel _cachedPlayerModel;
    private GameObject _deckOverlay;
    private GameObject _chooseCardOverlay;
    private bool _deckOverlayPending = false;
    private Dictionary<int, int> _pendingCostGroups = null;
    private Transform _cachedScrollView;
    private RectTransform _cachedScrollRT = null;
    private RectTransform _cachedDeckViewRT = null;
    private Il2CppStructArray<Vector3> _cornersBuf = null;
    private int   _overlayStabilizeFrames = 0;
    private bool  _deckOverlayFixed = false;
    private int   _levelupReopenDelay = 0;
    private int   _lastOverlayTotal = 0;
    private bool  _pendingLevelupClose = false;
    private int   _overlayCardCount = 0;
    private int   _cardCountCheckTimer = 0;

    public void Awake() { }

    static string GetCardSignature(ChooseCardModal modal)
    {
        var views = modal._cardChoiceViews;
        if (views == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var v in views)
            sb.Append(v?.CardConfig?.name ?? "null").Append("|");
        return sb.ToString();
    }

    public void Update()
    {
        // Animation des badges EVO arc-en-ciel : rotation lente du ring (couleurs qui défilent),
        // balayage rapide du shine (reflet qui orbite). Time.unscaledTime pour rester actif
        // même quand timeScale=0 (le level-up met le jeu en pause).
        if (_animBadges.Count > 0)
        {
            float t = Time.unscaledTime;
            float ringAngle = (t * 35f) % 360f;
            float shineAngle = (t * -120f) % 360f;
            for (int i = _animBadges.Count - 1; i >= 0; i--)
            {
                var ab = _animBadges[i];
                if (ab == null || ab.ring == null) { _animBadges.RemoveAt(i); continue; }
                try
                {
                    ab.ring.localEulerAngles = new Vector3(0f, 0f, ringAngle);
                    if (ab.shine != null) ab.shine.localEulerAngles = new Vector3(0f, 0f, shineAngle);
                }
                catch { _animBadges.RemoveAt(i); }
            }
        }

        if (_cachedModal == null)
        {
            if (--_searchCooldown > 0) return;
            _searchCooldown = 20;
            _cachedModal = UObject.FindObjectOfType<ChooseCardModal>();
        }
        var modal = _cachedModal;
        bool isOpen = modal != null && modal.IsOpen;
        if (isOpen)
        {
            string sig = GetCardSignature(modal);
            if (!_wasOpen || sig != _lastCardSignature)
            {
                _lastCardSignature = sig;
                OnModalOpened(modal);
            }
        }
        else
        {
            _lastCardSignature = "";
            if (_wasOpen)
            {
                if (_chooseCardOverlay != null) { UObject.Destroy(_chooseCardOverlay); _chooseCardOverlay = null; }
                _cachedModal = null;
                // Level-up vient de se fermer : démarrer le délai de recalcul COMPOSITION
                _pendingLevelupClose = true;
                if (_cachedDeckModal != null)
                    _levelupReopenDelay = 60;
            }
        }
        _wasOpen = isOpen;

        // DeckBox tracking
        if (_cachedDeckModal == null)
        {
            if (--_deckSearchCooldown <= 0)
            {
                _deckSearchCooldown = 20;
                _cachedDeckModal = UObject.FindObjectOfType<DeckBoxModal>();
                if (_cachedDeckModal != null)
                {
                    _cachedScrollView = null; _cachedScrollRT = null; _cachedDeckViewRT = null;
                    _deckAliveCheck = 0; _wasDeckOpen = false;
                    if (_pendingLevelupClose && _levelupReopenDelay == 0)
                    {
                        _levelupReopenDelay = 60;
                        _pendingLevelupClose = false;
                    }
                }
            }
        }
        if (_cachedDeckModal != null)
        {
            if (_overlayStabilizeFrames > 0) _overlayStabilizeFrames--;

            // Changement de langue : reconstruire le panneau (texte localisé)
            if (Plugin.LocaleRebuildPending)
            {
                Plugin.LocaleRebuildPending = false;
                if (_deckOverlay != null) { UObject.Destroy(_deckOverlay); _deckOverlay = null; }
                _deckOverlayFixed = false; _deckOverlayPending = false; _pendingCostGroups = null;
                _overlayStabilizeFrames = 0;
                if (_cachedDeckModal.IsOpen && !isOpen)
                    OnDeckBoxOpened(_cachedDeckModal, "locale-change");
            }

            if (_levelupReopenDelay > 0 && !isOpen)
            {
                if (--_levelupReopenDelay == 0)
                    OnDeckBoxOpened(_cachedDeckModal, "levelup-close");
            }
            bool isDeckOpen = _cachedDeckModal.IsOpen;

            // IsOpen=true → modal prêt (contenu chargé) : (re)construire l'overlay avec données fraîches.
            // Pas de garde _deckOverlay==null : sinon le panel resterait obsolète après altar/wild
            // pickup (modal caché mais toujours en scène, donc l'ancien panel persiste).
            if (isDeckOpen && !_wasDeckOpen && !_deckOverlayPending && !isOpen)
                OnDeckBoxOpened(_cachedDeckModal, "open-transition");
            _wasDeckOpen = isDeckOpen;

            if (_cachedScrollView == null)
            {
                var sv = FindNamedChild(_cachedDeckModal.gameObject.transform, "AdjustableScrollView", 10);
                if (sv != null) { _cachedScrollView = sv; _cachedScrollRT = sv.GetComponent<RectTransform>(); }
            }
            if (_cachedDeckViewRT == null)
            {
                var dvT = FindNamedChild(_cachedDeckModal.gameObject.transform, "DeckView");
                if (dvT != null) _cachedDeckViewRT = dvT.GetComponent<RectTransform>();
            }

            // Suivi temps-réel de la position de l'overlay (panneau horizontal uniquement)
            if (_deckOverlay != null && _cachedScrollRT != null && _cachedDeckViewRT != null && !_deckOverlayFixed)
            {
                _cornersBuf ??= new Il2CppStructArray<Vector3>(4);
                _cachedScrollRT.GetWorldCorners(_cornersBuf);
                Vector2 lp = Vector2.zero;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_cachedDeckViewRT, new Vector2(_cornersBuf[0].x, _cornersBuf[0].y), null, out lp))
                {
                    const float pH = 104f + 46f;
                    float yNew = lp.y - 5f - pH / 2f;
                    var prt = _deckOverlay.GetComponent<RectTransform>();
                    if (prt != null && Mathf.Abs(prt.anchoredPosition.y - yNew) > 0.5f)
                        prt.anchoredPosition = new Vector2(0f, yNew);
                }
            }

            // Masquer l'overlay deck quand le level-up est ouvert
            if (isOpen && _deckOverlay != null) { UObject.Destroy(_deckOverlay); _deckOverlay = null; }

            // Surveille l'apparition de nouvelles cartes (rendu différé après level-up)
            if (_deckOverlay != null && isDeckOpen && _levelupReopenDelay == 0)
            {
                if (++_cardCountCheckTimer >= 30)
                {
                    _cardCountCheckTimer = 0;
                    var ccPoll = FindNamedChild(_cachedDeckModal.gameObject.transform, "CardContainer", 10);
                    if (ccPoll != null && ccPoll.childCount != _overlayCardCount)
                    {
                        _levelupReopenDelay = 90;
                    }
                }
            }

            // Vérifier périodiquement si le modal est toujours actif (fermeture réelle)
            if (++_deckAliveCheck >= 30)
            {
                _deckAliveCheck = 0;
                if (UObject.FindObjectOfType<DeckBoxModal>() == null)
                {
                    if (_deckOverlay != null) { UObject.Destroy(_deckOverlay); _deckOverlay = null; }
                    _cachedDeckModal = null;
                    _cachedScrollView = null; _cachedScrollRT = null; _cachedDeckViewRT = null;
                    _deckOverlayFixed = false; _deckOverlayPending = false;
                    _pendingCostGroups = null;
                    _wasDeckOpen = false; _levelupReopenDelay = 0; _lastOverlayTotal = 0;
                    _overlayCardCount = 0; _cardCountCheckTimer = 0;
                }
            }
        }

        // Création de l'overlay différée d'un frame pour que le layout Unity soit calculé
        if (_deckOverlayPending && _cachedDeckModal != null && _pendingCostGroups != null)
        {
            _deckOverlayPending = false;
            if (!isOpen)
            {
                {
                    float yOverlay = -340f;
                    float xOverlay = 0f;
                    bool useSide = false;
                    try
                    {
                        var deckViewT  = FindNamedChild(_cachedDeckModal.gameObject.transform, "DeckView");
                        var scrollView = FindNamedChild(_cachedDeckModal.gameObject.transform, "AdjustableScrollView", 10);
                        if (scrollView != null && deckViewT != null)
                        {
                            Canvas.ForceUpdateCanvases();
                            var scrollRT = scrollView.GetComponent<RectTransform>();
                            var deckRT   = deckViewT.GetComponent<RectTransform>();
                            if (scrollRT != null && deckRT != null)
                            {
                                var corners = new Il2CppStructArray<Vector3>(4);
                                scrollRT.GetWorldCorners(corners);

                                // Calculer la position projetée du panneau horizontal sous le scroll
                                const float panelH = 104f + 46f;
                                Vector2 localPt = Vector2.zero;
                                RectTransformUtility.ScreenPointToLocalPointInRectangle(deckRT, new Vector2(corners[0].x, corners[0].y), null, out localPt);
                                float projY = localPt.y - 5f - panelH / 2f;
                                float projBottom = projY - panelH / 2f;

                                // Demi-hauteur du canvas racine (1080 par défaut)
                                float halfCanvasH = 540f;
                                var canvasRT = FindCanvasTransform(_cachedDeckModal.gameObject.transform)?.GetComponent<RectTransform>();
                                if (canvasRT != null && canvasRT.sizeDelta.y > 0f) halfCanvasH = canvasRT.sizeDelta.y / 2f;

                                int cardCount = 0;
                                var ccCheck = FindNamedChild(_cachedDeckModal.gameObject.transform, "CardContainer", 10);
                                if (ccCheck != null) cardCount = ccCheck.childCount;

                                // Side panel : si le scroll devient interne (>10 cartes, ~3+ rangées) ou
                                // si le panneau horizontal déborderait du canvas
                                useSide = cardCount > 10
                                       || projBottom < -(halfCanvasH + 10f);
                                if (!useSide)
                                {
                                    // Clamp : le bas du panneau doit rester dans le canvas
                                    float clampY = -(halfCanvasH - panelH / 2f - 4f);
                                    yOverlay = Mathf.Max(projY, clampY);
                                }
                                else
                                {
                                    // Grand deck : panneau vertical à droite du scroll view
                                    float worldRightX = corners[2].x;
                                    float worldCenterY = (corners[0].y + corners[1].y) / 2f;
                                    Vector2 rightLocal = Vector2.zero, centerLocal = Vector2.zero;
                                    RectTransformUtility.ScreenPointToLocalPointInRectangle(deckRT, new Vector2(worldRightX, worldCenterY), null, out rightLocal);
                                    RectTransformUtility.ScreenPointToLocalPointInRectangle(deckRT, new Vector2(corners[0].x, worldCenterY), null, out centerLocal);
                                    const float sideW = 240f;
                                    xOverlay = rightLocal.x + 20f + sideW / 2f;
                                    yOverlay = centerLocal.y;
                                }
                            }
                        }
                    }
                    catch (Exception posEx) { Plugin.Log.LogWarning($"[DeckBox] pos error: {posEx.Message}"); }

                    if (_deckOverlay != null) { UObject.Destroy(_deckOverlay); _deckOverlay = null; }
                    _deckOverlay = CreateManaPanel(_cachedDeckModal.gameObject, _pendingCostGroups, yOverlay, xOverlay, useSide);
                    _deckOverlayFixed = useSide;
                    _overlayStabilizeFrames = 30;
                    var ccTrack = FindNamedChild(_cachedDeckModal.gameObject.transform, "CardContainer", 10);
                    _overlayCardCount = ccTrack != null ? ccTrack.childCount : 0;
                    _cardCountCheckTimer = 0;
                }
            }
            _pendingCostGroups = null;
        }
    }

    // ─── Mana icon sprite cache ───────────────────────────────────────────────
    // _manaComboElementOrb  → "UI_Sprites_ComboElement_6" = le cercle coloré par coût
    // _manaComboElement     → "UI_Sprites_ComboElement_0" = l'arc décoratif par-dessus

    static readonly Dictionary<int, Sprite> _manaOrbSprites = new Dictionary<int, Sprite>();
    static readonly Dictionary<int, Color> _manaOrbColors = new Dictionary<int, Color>();
    static Sprite _arcSprite = null;
    static Sprite _modalFrameSprite = null;

    static void TryCacheManaIcons(Il2CppSystem.Collections.Generic.List<CardChoiceView> views)
    {
        foreach (var view in views)
        {
            var cfg = view?.CardConfig;
            if (cfg == null) continue;
            int cost = cfg.manaCost;
            if (!_manaOrbSprites.ContainsKey(cost))
            {
                var orbT = FindNamedChild(view.gameObject.transform, "_manaComboElementOrb", 15);
                var orbImg = orbT?.GetComponent<Image>();
                if (orbImg?.sprite != null)
                {
                    _manaOrbSprites[cost] = orbImg.sprite;
                    _manaOrbColors[cost] = orbImg.color;
                }
            }
            if (_arcSprite == null)
            {
                var arcT = FindNamedChild(view.gameObject.transform, "_manaComboElement", 15);
                var arcImg = arcT?.GetComponent<Image>();
                if (arcImg?.sprite != null) _arcSprite = arcImg.sprite;
            }
            // Cadre doré : sur le "Container" parent direct de la card view
            if (_modalFrameSprite == null && view.gameObject.transform.childCount > 0)
            {
                var containerImg = view.gameObject.transform.GetChild(0).GetComponent<Image>();
                if (containerImg != null && containerImg.type == Image.Type.Sliced && containerImg.sprite != null)
                    _modalFrameSprite = containerImg.sprite;
            }
        }
    }

    // Clé spéciale pour les cartes Wild ("W") dans le dictionnaire de coûts
    const int WILD_KEY = -99;

    void OnDeckBoxOpened(DeckBoxModal modal, string reason = "")
    {
        try
        {
            if (_overlayStabilizeFrames > 0 && reason == "open-transition")
                return;
            if (_deckOverlay != null)
            {
                UObject.Destroy(_deckOverlay);
                _deckOverlay = null;
            }
            if (_cachedModal != null && _cachedModal.IsOpen) return;

            // CardContainer : cache les sprites uniquement (draw pile visible) + fallback comptage
            var cardContainer = FindNamedChild(modal.gameObject.transform, "CardContainer", 10);
            if (cardContainer == null) { Plugin.Log.LogWarning("[DeckBox] CardContainer null"); return; }

            // Cadre doré : la modale DeckBox a son propre [frame] sliced (ex frame1_c4) toujours
            // présent dans la hiérarchie, indépendamment de l'état des cartes. On le récupère ici
            // si pas encore caché correctement (le sprite peut avoir été pollué par un fallback
            // sur des cartes en main qui exposent UI_sprites_52, qui n'est pas un cadre).
            bool frameOk = _modalFrameSprite != null
                && (_modalFrameSprite.name?.StartsWith("frame", System.StringComparison.OrdinalIgnoreCase) ?? false);
            if (!frameOk)
            {
                var modalFrame = FindFrameSpriteRecursive(modal.gameObject.transform, 15);
                if (modalFrame != null) _modalFrameSprite = modalFrame;
            }

            var fallbackGroups = new Dictionary<int, int>();

            for (int ci = 0; ci < cardContainer.childCount; ci++)
            {
                var cardT = cardContainer.GetChild(ci);

                // CardSelectionCardView → CardModel → CardView (le visuel, qui a _appliedCardConfig à jour
                // après évolution, et _costText avec le coût affiché à l'écran).
                var csv = cardT.GetComponent<CardSelectionCardView>();
                Nosebleed.Pancake.GameConfig.CardConfig csvCfg = null;
                CardModel csvCm = null;
                Nosebleed.Pancake.View.CardView csvView = null;
                if (csv != null)
                {
                    try
                    {
                        var m = csv.GetIl2CppType().GetMethod("get_CardModel");
                        var rawModel = m?.Invoke(csv, null);
                        if (rawModel != null)
                        {
                            csvCm = rawModel.TryCast<CardModel>();
                            if (csvCm != null)
                            {
                                csvCfg = csvCm.CardConfig;
                                try { csvView = csvCm.CardView; } catch { }
                            }
                        }
                    }
                    catch { }
                }
                // _appliedCardConfig = config réellement utilisée pour le rendu (post-évolution)
                if (csvView != null)
                {
                    try
                    {
                        var applied = csvView._appliedCardConfig;
                        if (applied != null) csvCfg = applied;
                    }
                    catch { }
                }
                string cfgName = csvCfg?.name ?? FindCardConfigName(cardT, 4);
                if (cfgName == null) continue;
                int cloneIdx = cfgName.IndexOf("(Clone)", System.StringComparison.Ordinal);
                if (cloneIdx > 0) cfgName = cfgName.Substring(0, cloneIdx).TrimEnd();

                var cfgNp = cfgName.Split('_');
                bool isCompanionCard = cfgNp.Length >= 2 && cfgNp[1].ToUpperInvariant() == "C";
                if (isCompanionCard) continue;

                bool isWild = cfgNp.Length == 3 || (cfgNp.Length >= 2 && cfgNp[1].ToUpperInvariant() == "E");
                int cost = -999;
                if (isWild)
                    cost = WILD_KEY;
                else
                {
                    // Source la plus fiable : _costText du CardView (texte exact affiché à l'écran)
                    if (csvView != null)
                    {
                        try
                        {
                            var ct = csvView._costText;
                            if (ct != null && int.TryParse(ct.text.Trim(), out int dispCost))
                                cost = dispCost;
                        }
                        catch { }
                    }
                    if (cost == -999 && csvCm != null)
                    {
                        try { cost = csvCm.GetCardCostTypeManaCost(false); }
                        catch { cost = -999; }
                    }
                    if (cost == -999 && csvCfg != null)
                        cost = csvCfg.manaCost;
                    if (cost == -999) continue;
                }

                // Cacher l'orbe du coût correspondant
                if (!_manaOrbSprites.ContainsKey(cost))
                {
                    Transform orbT;
                    if (cost == WILD_KEY)
                    {
                        orbT = FindNamedChild(cardT, "_W_cardCostBackgroundElement", 15)
                            ?? FindNamedChild(cardT, "_manaComboElementOrb", 15);
                    }
                    else
                        orbT = FindNamedChild(cardT, "_manaComboElementOrb", 15);

                    var orbImg = orbT?.GetComponent<Image>();
                    if (orbImg?.sprite != null)
                    {
                        _manaOrbSprites[cost] = orbImg.sprite;
                        _manaOrbColors[cost] = orbImg.color;
                    }
                }
                if (_modalFrameSprite == null && cardT.childCount > 0)
                {
                    var contImg = cardT.GetChild(0).GetComponent<Image>();
                    if (contImg != null && contImg.type == Image.Type.Sliced && contImg.sprite != null)
                        _modalFrameSprite = contImg.sprite;
                }

                fallbackGroups[cost] = fallbackGroups.TryGetValue(cost, out var cnt) ? cnt + 1 : 1;
            }

            // Récupérer le PlayerModel si pas encore mis en cache (combat sans level-up)
            if (_cachedPlayerModel == null)
                _cachedPlayerModel = UObject.FindObjectOfType<PlayerModel>();

            // Compter toutes les cartes du deck complet (pioche + main + défausse) depuis PlayerModel
            var costGroups = new Dictionary<int, int>();
            var allCards = _cachedPlayerModel?._allCards;

            if (allCards != null)
            {
                foreach (var cm in allCards)
                {
                    var cfg = cm?.CardConfig;
                    if (cfg == null || cfg.name == null) continue;
                    if (!cfg.name.StartsWith("Card_")) continue;
                    // _appliedCardConfig sur le CardView est la config réellement appliquée (post-évolution)
                    try
                    {
                        var cv = cm.CardView;
                        var applied = cv?._appliedCardConfig;
                        if (applied != null) cfg = applied;
                    }
                    catch { }
                    var np = cfg.name.Split('_');
                    if (np.Length >= 2 && np[1].ToUpperInvariant() == "C") continue; // compagnon
                    bool isWild = np.Length == 3 || (np.Length >= 2 && np[1].ToUpperInvariant() == "E");
                    int cost = -999;
                    if (isWild)
                    {
                        cost = WILD_KEY;
                        TryCacheWildSpriteFromModel(cm);
                    }
                    else
                    {
                        // _costText.text = coût exact affiché ; sinon GetCardCostTypeManaCost ; sinon base
                        try
                        {
                            var ct = cm.CardView?._costText;
                            if (ct != null && int.TryParse(ct.text.Trim(), out int dispC))
                                cost = dispC;
                        }
                        catch { }
                        if (cost == -999)
                        {
                            try { cost = cm.GetCardCostTypeManaCost(false); }
                            catch { cost = -999; }
                        }
                        if (cost == -999) cost = cfg.manaCost;
                        TryCacheOrbSpriteFromModel(cm, cost);
                    }
                    costGroups[cost] = costGroups.TryGetValue(cost, out var cnt) ? cnt + 1 : 1;
                }
            }
            if (costGroups.Count == 0)
                costGroups = fallbackGroups;

            if (costGroups.Count == 0) { Plugin.Log.LogWarning("[DeckBox] costGroups vide"); return; }
            int _newTotal = costGroups.Values.Sum();

            if (reason == "levelup-close" && _newTotal < _lastOverlayTotal)
                return;
            _lastOverlayTotal = _newTotal;

            _pendingCostGroups = costGroups;
            _deckOverlayPending = true;
        }
        catch (Exception ex) { Plugin.Log.LogError($"[DeckBox] {ex}"); }
    }

    // Met en cache le sprite Wild depuis le CardView d'un CardModel si pas encore caché.
    // Les cartes en main (rendues dans la scène) ont un CardView avec la bonne hiérarchie ;
    // les cartes de pioche/défausse ne sont pas toujours rendues hors de la modale DeckBox.
    static void TryCacheWildSpriteFromModel(CardModel cm)
    {
        if (cm == null || _manaOrbSprites.ContainsKey(WILD_KEY)) return;
        try
        {
            var cv = cm.CardView;
            if (cv == null) return;
            var t = cv.gameObject?.transform;
            if (t == null) return;
            var orbT = FindNamedChild(t, "_W_cardCostBackgroundElement", 15)
                    ?? FindNamedChild(t, "_manaComboElementOrb", 15);
            var orbImg = orbT?.GetComponent<Image>();
            if (orbImg?.sprite != null)
            {
                _manaOrbSprites[WILD_KEY] = orbImg.sprite;
                _manaOrbColors[WILD_KEY] = orbImg.color;
            }
        }
        catch { }
    }

    // Idem pour les coûts non-wild : si la modale DeckBox s'ouvre avec une pile vide
    // (toutes les cartes en main au début d'un combat), CardContainer ne fournit aucun
    // sprite à cacher → on retombe sur les CardViews de la main.
    static void TryCacheOrbSpriteFromModel(CardModel cm, int cost)
    {
        if (cm == null || _manaOrbSprites.ContainsKey(cost)) return;
        try
        {
            var cv = cm.CardView;
            if (cv == null) return;
            var t = cv.gameObject?.transform;
            if (t == null) return;
            var orbT = FindNamedChild(t, "_manaComboElementOrb", 15);
            var orbImg = orbT?.GetComponent<Image>();
            if (orbImg?.sprite != null)
            {
                _manaOrbSprites[cost] = orbImg.sprite;
                _manaOrbColors[cost] = orbImg.color;
            }
        }
        catch { }
    }

    // Recherche récursive d'un Image sliced dont le sprite commence par "frame".
    // Utilisé pour trouver le cadre doré de la modale DeckBox quand le CardContainer
    // est vide (deck en main → pas de carte à inspecter pour récupérer la sprite).
    static Sprite FindFrameSpriteRecursive(Transform t, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        var img = t.GetComponent<Image>();
        if (img != null && img.type == Image.Type.Sliced && img.sprite != null)
        {
            var n = img.sprite.name ?? "";
            if (n.StartsWith("frame", System.StringComparison.OrdinalIgnoreCase))
                return img.sprite;
        }
        for (int i = 0; i < t.childCount; i++)
        {
            var f = FindFrameSpriteRecursive(t.GetChild(i), maxDepth - 1);
            if (f != null) return f;
        }
        return null;
    }

    static Transform FindNamedChild(Transform t, string name, int maxDepth = 5)
    {
        if (maxDepth <= 0) return null;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c.name == name) return c;
            var found = FindNamedChild(c, name, maxDepth - 1);
            if (found != null) return found;
        }
        return null;
    }

    static string FindCardConfigName(Transform root, int maxDepth = 4)
    {
        if (maxDepth <= 0) return null;
        if (root.name.StartsWith("Card_")) return root.name;
        for (int i = 0; i < root.childCount; i++)
        {
            var found = FindCardConfigName(root.GetChild(i), maxDepth - 1);
            if (found != null) return found;
        }
        return null;
    }

    static (string main, string sub) GetCompositionLabel()
    {
        string full = Plugin.CurrentLangCode ?? "";

        // Variantes chinoises : lire le code complet
        if (full.StartsWith("zh"))
            return full.Contains("hant") ? ("牌組構成", "(不含同伴卡)") : ("牌组构成", "(不含同伴卡)");

        // Code normalisé : juste la partie langue ("fr-FR" → "fr", "pt-BR" → "pt")
        string lang = full.Length > 0 ? full.Split('-')[0] : "";

        switch (lang)
        {
            case "fr": return ("COMPOSITION", "(hors cartes compagnons)");
            case "de": return ("ZUSAMMENSETZUNG", "(ohne Begleiterkarten)");
            case "it": return ("COMPOSIZIONE", "(escl. carte compagno)");
            case "es": return ("COMPOSICIÓN", "(excl. cartas de compañero)");
            case "ru": return ("СОСТАВ", "(без карт спутника)");
            case "pl": return ("SKŁAD TALII", "(bez kart towarzyszy)");
            case "pt": return ("COMPOSIÇÃO", "(excl. cartas de companheiro)");
            case "ja": return ("デッキ構成", "(仲間カードを除く)");
            case "ko": return ("덱 구성", "(동료 카드 제외)");
            default:   return ("COMPOSITION", "(excl. companion cards)");
        }
    }

    static Transform FindCanvasTransform(Transform t, int maxDepth = 5)
    {
        if (maxDepth <= 0) return null;
        if (t.GetComponent<Canvas>() != null && t.GetComponent<RectTransform>() != null) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var found = FindCanvasTransform(t.GetChild(i), maxDepth - 1);
            if (found != null) return found;
        }
        return null;
    }

    static GameObject CreateManaPanel(GameObject modalGo, Dictionary<int, int> costGroups, float yOffset, float xOffset = 0f, bool vertical = false)
    {
        var chooseACard = FindNamedChild(modalGo.transform, "ChooseACard");
        var canvasT     = FindCanvasTransform(modalGo.transform);
        var target = chooseACard ?? canvasT ?? modalGo.transform;

        var panel = new GameObject("VCManaOverlay");
        panel.layer = target.gameObject.layer;
        panel.transform.SetParent(target, false);
        panel.transform.SetAsLastSibling();

        var rt = panel.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(xOffset, yOffset);

        // Wild (WILD_KEY=-99) trié en premier
        var sorted = costGroups.OrderBy(kv => kv.Key == WILD_KEY ? int.MinValue : kv.Key).ToList();
        bool allData = sorted.Count > 0;

        if (allData && vertical)
        {
            // Layout vertical (panneau latéral pour grand deck)
            const float bSz = 84f, cW = 110f, bGap = 12f, padX = 18f, padBot = 14f, lH = 60f, rH = 88f, rGap = 8f;
            float tw = padX + bSz + bGap + cW + padX;
            float th = lH + 10f + sorted.Count * (rH + rGap) - rGap + padBot;
            rt.sizeDelta = new Vector2(tw, th);

            var vBgFill = new GameObject("BgFill"); vBgFill.transform.SetParent(panel.transform, false);
            var vBfrt = vBgFill.AddComponent<RectTransform>();
            vBfrt.anchorMin = Vector2.zero; vBfrt.anchorMax = Vector2.one; vBfrt.offsetMin = vBfrt.offsetMax = Vector2.zero;
            vBgFill.AddComponent<Image>().color = new Color(0.294f, 0.310f, 0.455f, 0.97f);
            if (_modalFrameSprite != null) {
                var vFr = new GameObject("BgFrame"); vFr.transform.SetParent(panel.transform, false);
                var vFrrt = vFr.AddComponent<RectTransform>(); vFrrt.anchorMin = Vector2.zero; vFrrt.anchorMax = Vector2.one; vFrrt.offsetMin = vFrrt.offsetMax = Vector2.zero;
                var vFrI = vFr.AddComponent<Image>(); vFrI.sprite = _modalFrameSprite; vFrI.type = Image.Type.Sliced;
            }
            var (vMain, vSub) = GetCompositionLabel();
            var vTitleGo = new GameObject("Title"); vTitleGo.transform.SetParent(panel.transform, false);
            var vTRT = vTitleGo.AddComponent<RectTransform>(); vTRT.anchorMin = new Vector2(0f,1f); vTRT.anchorMax = new Vector2(1f,1f); vTRT.pivot = new Vector2(0.5f,1f); vTRT.anchoredPosition = new Vector2(0f,-6f); vTRT.sizeDelta = new Vector2(-8f,26f);
            var vTTmp = vTitleGo.AddComponent<TextMeshProUGUI>(); vTTmp.text = vMain; vTTmp.fontSize = 22f; vTTmp.fontStyle = FontStyles.Bold; vTTmp.alignment = TextAlignmentOptions.Center; vTTmp.color = new Color(1f,0.92f,0.55f,1f); vTTmp.enableWordWrapping = false; vTTmp.overflowMode = TextOverflowModes.Overflow;
            var vSubGo = new GameObject("Sub"); vSubGo.transform.SetParent(panel.transform, false);
            var vSRT = vSubGo.AddComponent<RectTransform>(); vSRT.anchorMin = new Vector2(0f,1f); vSRT.anchorMax = new Vector2(1f,1f); vSRT.pivot = new Vector2(0.5f,1f); vSRT.anchoredPosition = new Vector2(0f,-32f); vSRT.sizeDelta = new Vector2(-8f,18f);
            var vSTmp = vSubGo.AddComponent<TextMeshProUGUI>(); vSTmp.text = vSub; vSTmp.fontSize = 14f; vSTmp.alignment = TextAlignmentOptions.Center; vSTmp.color = new Color(0.78f,0.78f,0.85f,0.9f); vSTmp.enableWordWrapping = false; vSTmp.overflowMode = TextOverflowModes.Overflow;
            var vSep = new GameObject("Sep"); vSep.transform.SetParent(panel.transform, false);
            var vSepRT = vSep.AddComponent<RectTransform>(); vSepRT.anchorMin = new Vector2(0.03f,1f); vSepRT.anchorMax = new Vector2(0.97f,1f); vSepRT.pivot = new Vector2(0.5f,1f); vSepRT.anchoredPosition = new Vector2(0f,-lH); vSepRT.sizeDelta = new Vector2(0f,1f);
            vSep.AddComponent<Image>().color = new Color(1f,0.92f,0.55f,0.35f);

            float bX = -tw/2f + padX + bSz/2f;
            float cX = -tw/2f + padX + bSz + bGap;
            float rowY = th/2f - lH - 10f - rH/2f;
            foreach (var kv in sorted)
            {
                var bGo = new GameObject("Badge"); bGo.transform.SetParent(panel.transform, false);
                var brt = bGo.AddComponent<RectTransform>(); brt.anchorMin = brt.anchorMax = new Vector2(0.5f,0.5f); brt.pivot = new Vector2(0.5f,0.5f); brt.anchoredPosition = new Vector2(bX, rowY); brt.sizeDelta = new Vector2(bSz, bSz);
                if (kv.Key == WILD_KEY && _manaOrbSprites.ContainsKey(kv.Key)) {
                    var wBg = new GameObject("WBg"); wBg.transform.SetParent(bGo.transform, false);
                    var wBgR = wBg.AddComponent<RectTransform>(); wBgR.anchorMin = wBgR.anchorMax = new Vector2(0.5f,0.5f); wBgR.pivot = new Vector2(0.5f,0.5f); wBgR.anchoredPosition = new Vector2(-1f,1f); wBgR.sizeDelta = new Vector2(bSz,bSz);
                    wBg.AddComponent<Image>().sprite = _manaOrbSprites[kv.Key];
                    var wFg = new GameObject("WFg"); wFg.transform.SetParent(bGo.transform, false);
                    var wFgR = wFg.AddComponent<RectTransform>(); wFgR.anchorMin = wFgR.anchorMax = new Vector2(0.5f,0.5f); wFgR.pivot = new Vector2(0.5f,0.5f); wFgR.anchoredPosition = Vector2.zero; wFgR.sizeDelta = new Vector2(bSz,bSz);
                    var wFgI = wFg.AddComponent<Image>(); wFgI.sprite = _manaOrbSprites[kv.Key]; wFgI.color = _manaOrbColors.TryGetValue(kv.Key, out var wc4) ? wc4 : new Color(0f,0.23f,0.58f,1f);
                } else {
                    var bI = bGo.AddComponent<Image>();
                    if (_manaOrbSprites.TryGetValue(kv.Key, out var sp2)) { bI.sprite = sp2; if (_manaOrbColors.TryGetValue(kv.Key, out var bc2)) bI.color = bc2; }
                    else if (_manaOrbSprites.Count > 0) { bI.sprite = _manaOrbSprites.First().Value; bI.color = _manaOrbColors.Count > 0 ? _manaOrbColors.First().Value : Color.white; }
                    var nGo = new GameObject("Num"); nGo.transform.SetParent(bGo.transform, false);
                    var nR = nGo.AddComponent<RectTransform>(); nR.anchorMin = Vector2.zero; nR.anchorMax = Vector2.one; nR.offsetMin = nR.offsetMax = Vector2.zero;
                    var nT = nGo.AddComponent<TextMeshProUGUI>(); nT.text = kv.Key == WILD_KEY ? "W" : kv.Key.ToString(); nT.fontSize = 60f; nT.fontStyle = FontStyles.Bold; nT.alignment = TextAlignmentOptions.Center; nT.color = Color.white; nT.enableWordWrapping = false; nT.overflowMode = TextOverflowModes.Overflow; nT.margin = Vector4.zero;
                }
                var cGo = new GameObject("Cnt"); cGo.transform.SetParent(panel.transform, false);
                var cR = cGo.AddComponent<RectTransform>(); cR.anchorMin = cR.anchorMax = new Vector2(0.5f,0.5f); cR.pivot = new Vector2(0f,0.5f); cR.anchoredPosition = new Vector2(cX, rowY); cR.sizeDelta = new Vector2(cW, rH);
                var cT = cGo.AddComponent<TextMeshProUGUI>(); cT.text = $"x{kv.Value}"; cT.fontSize = 36f; cT.fontStyle = FontStyles.Bold; cT.alignment = TextAlignmentOptions.MidlineLeft; cT.color = Color.white;
                rowY -= (rH + rGap);
            }
        }
        else if (allData)
        {
            const float badgeSz = 80f, cntW = 64f, gap = 20f, pad = 22f, labelH = 46f;
            float totalW = sorted.Count * (badgeSz + cntW + gap) - gap + pad * 2;
            rt.sizeDelta = new Vector2(totalW, 104f + labelH);

            // Fond bleu-gris
            var bgFill = new GameObject("BgFill");
            bgFill.transform.SetParent(panel.transform, false);
            var bfrt = bgFill.AddComponent<RectTransform>();
            bfrt.anchorMin = Vector2.zero; bfrt.anchorMax = Vector2.one;
            bfrt.offsetMin = bfrt.offsetMax = Vector2.zero;
            bgFill.AddComponent<Image>().color = new Color(0.294f, 0.310f, 0.455f, 0.97f);

            // Cadre doré (même sprite que les cartes, 9-slice)
            if (_modalFrameSprite != null)
            {
                var bgFrame = new GameObject("BgFrame");
                bgFrame.transform.SetParent(panel.transform, false);
                var bfr2 = bgFrame.AddComponent<RectTransform>();
                bfr2.anchorMin = Vector2.zero; bfr2.anchorMax = Vector2.one;
                bfr2.offsetMin = bfr2.offsetMax = Vector2.zero;
                var fi = bgFrame.AddComponent<Image>();
                fi.sprite = _modalFrameSprite;
                fi.type = Image.Type.Sliced;
            }

            // Titre + sous-titre (zone haute du panel)
            var (mainText, subText) = GetCompositionLabel();
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panel.transform, false);
            var titleRT = titleGo.AddComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f); titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f);
            titleRT.anchoredPosition = new Vector2(0f, -4f);
            titleRT.sizeDelta = new Vector2(-8f, 24f);
            var titleTMP = titleGo.AddComponent<TextMeshProUGUI>();
            titleTMP.text = mainText;
            titleTMP.fontSize = 22f;
            titleTMP.fontStyle = FontStyles.Bold;
            titleTMP.alignment = TextAlignmentOptions.Center;
            titleTMP.color = new Color(1f, 0.92f, 0.55f, 1f);
            titleTMP.enableWordWrapping = false;
            titleTMP.overflowMode = TextOverflowModes.Overflow;

            var subGo = new GameObject("Subtitle");
            subGo.transform.SetParent(panel.transform, false);
            var subRT = subGo.AddComponent<RectTransform>();
            subRT.anchorMin = new Vector2(0f, 1f); subRT.anchorMax = new Vector2(1f, 1f);
            subRT.pivot = new Vector2(0.5f, 1f);
            subRT.anchoredPosition = new Vector2(0f, -27f);
            subRT.sizeDelta = new Vector2(-8f, 18f);
            var subTMP = subGo.AddComponent<TextMeshProUGUI>();
            subTMP.text = subText;
            subTMP.fontSize = 14f;
            subTMP.alignment = TextAlignmentOptions.Center;
            subTMP.color = new Color(0.78f, 0.78f, 0.85f, 0.9f);
            subTMP.enableWordWrapping = false;
            subTMP.overflowMode = TextOverflowModes.Overflow;

            // Séparateur
            var sepGo = new GameObject("Sep");
            sepGo.transform.SetParent(panel.transform, false);
            var sepRT = sepGo.AddComponent<RectTransform>();
            sepRT.anchorMin = new Vector2(0.03f, 1f); sepRT.anchorMax = new Vector2(0.97f, 1f);
            sepRT.pivot = new Vector2(0.5f, 1f);
            sepRT.anchoredPosition = new Vector2(0f, -labelH);
            sepRT.sizeDelta = new Vector2(0f, 1f);
            sepGo.AddComponent<Image>().color = new Color(1f, 0.92f, 0.55f, 0.35f);

            float badgeY = -labelH / 2f;
            float x = -totalW / 2f + pad;
            foreach (var kv in sorted)
            {
                var badgeGo = new GameObject("Badge");
                badgeGo.transform.SetParent(panel.transform, false);
                var brt = badgeGo.AddComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                brt.pivot = new Vector2(0f, 0.5f);
                brt.anchoredPosition = new Vector2(x, badgeY);
                brt.sizeDelta = new Vector2(badgeSz, badgeSz);

                if (kv.Key == WILD_KEY && _manaOrbSprites.ContainsKey(WILD_KEY))
                {
                    // Couche 0 : liseré gris foncé (même sprite légèrement plus grand, rendu en dessous)
                    var shadowGo = new GameObject("WildShadow");
                    shadowGo.transform.SetParent(badgeGo.transform, false);
                    var shadowrt = shadowGo.AddComponent<RectTransform>();
                    shadowrt.anchorMin = shadowrt.anchorMax = new Vector2(0.5f, 0.5f);
                    shadowrt.pivot = new Vector2(0.5f, 0.5f);
                    shadowrt.anchoredPosition = Vector2.zero;
                    shadowrt.sizeDelta = new Vector2(badgeSz + 12f, badgeSz + 12f);
                    var shadowImg = shadowGo.AddComponent<Image>();
                    shadowImg.sprite = _manaOrbSprites[kv.Key];
                    shadowImg.color = new Color(0.1f, 0.1f, 0.15f, 0.95f);

                    // Reproduction de l'effet carte Wild : blanc décalé haut-gauche (liseré) + bleu centré par-dessus
                    var bgGo = new GameObject("WildBg");
                    bgGo.transform.SetParent(badgeGo.transform, false);
                    var bgrt2 = bgGo.AddComponent<RectTransform>();
                    bgrt2.anchorMin = bgrt2.anchorMax = new Vector2(0.5f, 0.5f);
                    bgrt2.pivot = new Vector2(0.5f, 0.5f);
                    bgrt2.anchoredPosition = new Vector2(-2f, 2f);
                    bgrt2.sizeDelta = new Vector2(badgeSz, badgeSz);
                    bgGo.AddComponent<Image>().sprite = _manaOrbSprites[kv.Key]; // blanc (couleur par défaut)

                    var fgGo = new GameObject("WildFg");
                    fgGo.transform.SetParent(badgeGo.transform, false);
                    var fgrt = fgGo.AddComponent<RectTransform>();
                    fgrt.anchorMin = fgrt.anchorMax = new Vector2(0.5f, 0.5f);
                    fgrt.pivot = new Vector2(0.5f, 0.5f);
                    fgrt.anchoredPosition = Vector2.zero;
                    fgrt.sizeDelta = new Vector2(badgeSz, badgeSz);
                    var fgImg = fgGo.AddComponent<Image>();
                    fgImg.sprite = _manaOrbSprites[kv.Key];
                    fgImg.color = _manaOrbColors.TryGetValue(kv.Key, out var wc) ? wc : new Color(0f, 0.23f, 0.58f, 1f);
                }
                else
                {
                    var badgeImg = badgeGo.AddComponent<Image>();
                    if (_manaOrbSprites.TryGetValue(kv.Key, out var orbSpr))
                    {
                        badgeImg.sprite = orbSpr;
                        if (_manaOrbColors.TryGetValue(kv.Key, out var orbColor)) badgeImg.color = orbColor;
                    }
                    else
                    {
                        // Sprite non caché : même orbe que les autres coûts, même couleur
                        if (_manaOrbSprites.Count > 0)
                            badgeImg.sprite = _manaOrbSprites.First().Value;
                        badgeImg.color = _manaOrbColors.Count > 0 ? _manaOrbColors.First().Value : Color.white;
                    }

                    var numGo = new GameObject("Num");
                    numGo.transform.SetParent(badgeGo.transform, false);
                    var nrt = numGo.AddComponent<RectTransform>();
                    nrt.anchorMin = Vector2.zero; nrt.anchorMax = Vector2.one;
                    nrt.offsetMin = nrt.offsetMax = Vector2.zero;
                    var ntmp = numGo.AddComponent<TextMeshProUGUI>();
                    ntmp.text = kv.Key == WILD_KEY ? "W" : kv.Key.ToString();
                    ntmp.fontSize = 56f;
                    ntmp.fontStyle = FontStyles.Bold;
                    ntmp.alignment = TextAlignmentOptions.Center;
                    ntmp.color = Color.white;
                    ntmp.enableWordWrapping = false;
                    ntmp.overflowMode = TextOverflowModes.Overflow;
                    ntmp.margin = Vector4.zero;
                }

                var cntGo = new GameObject("Cnt");
                cntGo.transform.SetParent(panel.transform, false);
                var crt = cntGo.AddComponent<RectTransform>();
                crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
                crt.pivot = new Vector2(0f, 0.5f);
                crt.anchoredPosition = new Vector2(x + badgeSz + 4f, badgeY);
                crt.sizeDelta = new Vector2(cntW, 56f);
                var ctmp = cntGo.AddComponent<TextMeshProUGUI>();
                ctmp.text = $"x{kv.Value}";
                ctmp.fontSize = 30f;
                ctmp.fontStyle = FontStyles.Bold;
                ctmp.alignment = TextAlignmentOptions.MidlineLeft;
                ctmp.color = Color.white;

                x += badgeSz + cntW + gap;
            }
        }
        else
        {
            rt.sizeDelta = new Vector2(300f, 40f);
            var bgFill = new GameObject("BgFill");
            bgFill.transform.SetParent(panel.transform, false);
            var bfrt = bgFill.AddComponent<RectTransform>();
            bfrt.anchorMin = Vector2.zero; bfrt.anchorMax = Vector2.one;
            bfrt.offsetMin = bfrt.offsetMax = Vector2.zero;
            bgFill.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.72f);
            var label = new GameObject("Label");
            label.transform.SetParent(panel.transform, false);
            var lrt = label.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(6f, 0f); lrt.offsetMax = new Vector2(-6f, 0f);
            var tmp = label.AddComponent<TextMeshProUGUI>();
            tmp.text = string.Join("   ", sorted.Select(kv => $"{kv.Key}: {kv.Value}"));
            tmp.fontSize = 14f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(1f, 0.92f, 0.55f, 1f);
        }

        return panel;
    }

    void OnModalOpened(ChooseCardModal modal)
    {
        try
        {
            var playerModel = modal._playerModel;
            if (playerModel == null) return;
            _cachedPlayerModel = playerModel;

            var views = modal._cardChoiceViews;
            if (views == null) return;

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
                        countByBase[bn] = countByBase.TryGetValue(bn, out var cnt) ? cnt + 1 : 1;
                    }
                    // Inclure toutes les cartes du deck, y compris celles actuellement offertes :
                    // c'est nécessaire pour détecter la redondance quand le joueur possède déjà
                    // un duplicata de la carte offerte. La logique anyFromDeck de CheckCombo est
                    // robuste à ça via le filtre !CardMatchesComp(offered, c).
                    ownedConfigs.Add(cfg);
                }

            TryCacheManaIcons(views);

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

                if (cfg.name == null) continue;
                // Note : on ne skippe PAS les cartes déjà possédées. CheckCombo doit pouvoir
                // détecter la redondance même sur un duplicata offert (combo déjà couvert).
                var (hasCombo, evoName, isRedundant) = CheckCombo(cfg, ownedConfigs);
                if (hasCombo)
                {
                    AddBadge(view.gameObject, evoName, isRedundant);
                }
            }

            // Overlay mana cost du deck courant
            if (_chooseCardOverlay != null) { UObject.Destroy(_chooseCardOverlay); _chooseCardOverlay = null; }
            if (allCards != null)
            {
                var manaCosts = new Dictionary<int, int>();
                foreach (var cm in allCards)
                {
                    var cfg = cm?.CardConfig;
                    if (cfg == null || cfg.name == null || !cfg.name.StartsWith("Card_")) continue;
                    var np2 = cfg.name.Split('_');
                    bool isWild2 = np2.Length == 3 || (np2.Length >= 2 && np2[1].ToUpperInvariant() == "E");
                    int gemMod2 = 0;
                    try {
                        var gmods2 = cm.CardGems?._gemManaModifiers;
                        if (gmods2 != null) foreach (var kvp in gmods2) gemMod2 += kvp.Value.Count * kvp.Value.Amount;
                    } catch { }
                    int cost = isWild2 ? WILD_KEY : cfg.manaCost + gemMod2;
                    if (isWild2) TryCacheWildSpriteFromModel(cm);
                    else TryCacheOrbSpriteFromModel(cm, cost);
                    manaCosts[cost] = manaCosts.TryGetValue(cost, out var cnt) ? cnt + 1 : 1;
                }
                if (manaCosts.Count > 0)
                    _chooseCardOverlay = CreateManaPanel(modal.gameObject, manaCosts, -440f);
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

    // Retourne (hasCombo, evoName, isRedundant). isRedundant = true si tous les composants
    // sont déjà dans le deck sans avoir besoin de piocher la carte offerte (combo couvert).
    static (bool hasCombo, string evoName, bool isRedundant) CheckCombo(CardConfig offered, List<CardConfig> ownedConfigs)
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
                    bool allInDeck = comps.All(c => OwnsComp(c, ownedConfigs));
                    var evolved = offered.EvolvedCardConfig;
                    return (true, evolved?.Name ?? "?", allInDeck);
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
                bool allInDeck = comps.All(c => OwnsComp(c, ownedConfigs));
                var evolved = owned.EvolvedCardConfig;
                return (true, evolved?.Name ?? "?", allInDeck);
            }
        }

        return (false, "", false);
    }

    const string BadgeName = "VCComboBadge";
    static Sprite _ringSpriteRainbow;
    static Sprite _ringSpriteSilver;
    static Sprite _evoTextSprite;
    static Sprite _shineSprite;
    static Sprite _checkmarkSprite;

    // Ring + glow + intérieur sombre, sans le texte EVO. Tournable indépendamment.
    static Sprite GetBadgeRingSprite(bool silver = false)
    {
        if (silver && _ringSpriteSilver != null) return _ringSpriteSilver;
        if (!silver && _ringSpriteRainbow != null) return _ringSpriteRainbow;

        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        float cx = sz / 2f, cy = sz / 2f;
        var pixels = new Color[sz * sz];

        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            Color col = Color.clear;

            if (dist < 32f)
            {
                float angle = Mathf.Atan2(dy, dx);
                float dirLight = (Mathf.Cos(angle + Mathf.PI * 0.3f) + 1f) * 0.5f;

                if (dist >= 29f)
                {
                    float t = 1f - (dist - 29f) / 3f;
                    col = silver
                        ? new Color(0.40f, 0.45f, 0.55f, t * t * 0.55f)
                        : new Color(0.55f, 0.30f, 0.70f, t * t * 0.55f);
                }
                else if (dist >= 19.5f)
                {
                    float ringPos = (dist - 19.5f) / 9.5f;
                    float band = (Mathf.Sin(ringPos * Mathf.PI * 3.5f - 0.3f) + 1f) * 0.5f;
                    float edgeBoost = Mathf.Pow(Mathf.Abs(ringPos * 2f - 1f), 1.5f);
                    float brightness = band * 0.32f + dirLight * 0.33f + edgeBoost * 0.28f + 0.07f;
                    brightness = Mathf.Clamp01(brightness);

                    if (silver)
                    {
                        col = new Color(
                            Mathf.Lerp(0.42f, 0.96f, brightness),
                            Mathf.Lerp(0.46f, 0.98f, brightness),
                            Mathf.Lerp(0.55f, 1.00f, brightness),
                            1f);
                    }
                    else
                    {
                        // Hue cyclée par l'angle : la rotation du sprite fait défiler les couleurs.
                        float hue = ((angle + Mathf.PI) / (2f * Mathf.PI)) % 1f;
                        Color hsv = Color.HSVToRGB(hue, 0.92f, 1f);
                        col = new Color(
                            Mathf.Lerp(hsv.r * 0.30f, hsv.r, brightness),
                            Mathf.Lerp(hsv.g * 0.30f, hsv.g, brightness),
                            Mathf.Lerp(hsv.b * 0.30f, hsv.b, brightness),
                            1f);
                    }
                }
                else if (dist >= 17.5f)
                {
                    float t = (dist - 17.5f) / 2f;
                    col = silver
                        ? new Color(0.06f, 0.06f, 0.10f, t * 0.9f)
                        : new Color(0.07f, 0.03f, 0.10f, t * 0.9f);
                }
                else
                {
                    col = silver
                        ? new Color(0.05f, 0.05f, 0.08f, 0.97f)
                        : new Color(0.05f, 0.03f, 0.08f, 0.97f);
                }
            }
            pixels[y * sz + x] = col;
        }

        tex.SetPixels(pixels);
        tex.Apply();
        var sprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        if (silver) _ringSpriteSilver = sprite; else _ringSpriteRainbow = sprite;
        return sprite;
    }

    // Texte EVO blanc avec ombre, sur fond transparent. Statique, posé par-dessus le ring.
    static Sprite GetEvoTextSprite()
    {
        if (_evoTextSprite != null) return _evoTextSprite;
        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[sz * sz];

        const int scale = 3, gw = 5, gh = 7;
        byte[][][] glyphs = {
            new byte[][]{
                new byte[]{1,1,1,1,1}, new byte[]{1,0,0,0,0}, new byte[]{1,0,0,0,0},
                new byte[]{1,1,1,1,0}, new byte[]{1,0,0,0,0}, new byte[]{1,0,0,0,0}, new byte[]{1,1,1,1,1},
            },
            new byte[][]{
                new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1}, new byte[]{0,1,0,1,0}, new byte[]{0,1,0,1,0}, new byte[]{0,0,1,0,0},
            },
            new byte[][]{
                new byte[]{0,1,1,1,0}, new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1},
                new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1}, new byte[]{1,0,0,0,1}, new byte[]{0,1,1,1,0},
            },
        };

        int letterW = gw * scale, letterH = gh * scale, gap = scale;
        int totalW = 3 * letterW + 2 * gap;
        int xOff = (sz - totalW) / 2;
        int yOff = (sz - letterH) / 2;

        // Ombre noire (offset 1px)
        for (int g = 0; g < 3; g++)
        {
            int gxStart = xOff + g * (letterW + gap);
            for (int r = 0; r < gh; r++)
            for (int col2 = 0; col2 < gw; col2++)
            if (glyphs[g][r][col2] != 0)
            for (int sy = 0; sy < scale; sy++)
            for (int sx = 0; sx < scale; sx++)
            {
                int px = gxStart + col2 * scale + sx + 1;
                int py = yOff + (gh - 1 - r) * scale + sy - 1;
                if (px >= 0 && px < sz && py >= 0 && py < sz)
                    pixels[py * sz + px] = new Color(0f, 0f, 0f, 0.92f);
            }
        }

        // Texte blanc
        for (int g = 0; g < 3; g++)
        {
            int gxStart = xOff + g * (letterW + gap);
            for (int r = 0; r < gh; r++)
            for (int col2 = 0; col2 < gw; col2++)
            if (glyphs[g][r][col2] != 0)
            for (int sy = 0; sy < scale; sy++)
            for (int sx = 0; sx < scale; sx++)
            {
                int px = gxStart + col2 * scale + sx;
                int py = yOff + (gh - 1 - r) * scale + sy;
                if (px >= 0 && px < sz && py >= 0 && py < sz)
                    pixels[py * sz + px] = new Color(1f, 1f, 1f, 1f);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        _evoTextSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        return _evoTextSprite;
    }

    // Balayage de lumière : un arc clair concentré qui ne rend que sur la zone du ring.
    // Posé en dernier sibling (par-dessus tout) et tourné rapidement = effet foil/holographique.
    static Sprite GetShineSprite()
    {
        if (_shineSprite != null) return _shineSprite;
        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        float cx = sz / 2f, cy = sz / 2f;
        var pixels = new Color[sz * sz];

        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            Color col = Color.clear;

            if (dist >= 18.5f && dist <= 30.5f)
            {
                float angle = Mathf.Atan2(dy, dx);
                // Lobe étroit centré sur le haut (angle = π/2)
                float fromTop = angle - Mathf.PI / 2f;
                while (fromTop > Mathf.PI) fromTop -= 2 * Mathf.PI;
                while (fromTop < -Mathf.PI) fromTop += 2 * Mathf.PI;
                float angleFalloff = Mathf.Cos(fromTop * 1.2f);
                angleFalloff = Mathf.Clamp01(angleFalloff);
                angleFalloff = Mathf.Pow(angleFalloff, 5f);

                // Atténuation radiale : pic au milieu du ring, fondu vers les bords
                float ringPos = (dist - 18.5f) / 12f;
                float distFalloff = 1f - Mathf.Abs(ringPos * 2f - 1f);
                distFalloff = Mathf.Pow(Mathf.Clamp01(distFalloff), 1.4f);

                float intensity = angleFalloff * distFalloff;
                col = new Color(1f, 1f, 1f, intensity * 0.85f);
            }
            pixels[y * sz + x] = col;
        }

        tex.SetPixels(pixels);
        tex.Apply();
        _shineSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        return _shineSprite;
    }

    // Petite pastille verte avec un ✓ blanc, posée en surimpression sur le badge EVO bis.
    static Sprite GetCheckmarkSprite()
    {
        if (_checkmarkSprite != null) return _checkmarkSprite;
        const int sz = 32;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[sz * sz];
        float cx = sz / 2f, cy = sz / 2f, radius = 14f;

        var greenLight = new Color(0.32f, 0.85f, 0.36f, 1f);
        var greenDark  = new Color(0.10f, 0.50f, 0.18f, 1f);

        // Disque vert avec léger éclairage directionnel + AA sur le bord
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= radius + 0.5f)
            {
                float angle = Mathf.Atan2(dy, dx);
                float dirLight = (Mathf.Cos(angle + Mathf.PI * 0.3f) + 1f) * 0.5f;
                float t = Mathf.Clamp01(dirLight * 0.55f + (1f - dist / radius) * 0.45f);
                var col = Color.Lerp(greenDark, greenLight, t);
                float aaEdge = Mathf.Clamp01(radius - dist + 0.5f);
                pixels[y * sz + x] = new Color(col.r, col.g, col.b, aaEdge);
            }
        }

        // ✓ blanc en pixel art (10×7, scale 2 = 20×14 réels)
        byte[][] check = {
            new byte[]{0,0,0,0,0,0,0,0,1,0},
            new byte[]{0,0,0,0,0,0,0,1,1,0},
            new byte[]{0,0,0,0,0,0,1,1,0,0},
            new byte[]{1,0,0,0,0,1,1,0,0,0},
            new byte[]{1,1,0,0,1,1,0,0,0,0},
            new byte[]{0,1,1,1,1,0,0,0,0,0},
            new byte[]{0,0,1,1,0,0,0,0,0,0},
        };
        const int chW = 10, chH = 7, scaleC = 2;
        int xOff = (sz - chW * scaleC) / 2;
        int yOff = (sz - chH * scaleC) / 2;

        // Ombre + tracé blanc
        for (int pass = 0; pass < 2; pass++)
        for (int r = 0; r < chH; r++)
        for (int c = 0; c < chW; c++)
        if (check[r][c] != 0)
        for (int sy = 0; sy < scaleC; sy++)
        for (int sx = 0; sx < scaleC; sx++)
        {
            int px = xOff + c * scaleC + sx + (pass == 0 ? 1 : 0);
            int py = yOff + (chH - 1 - r) * scaleC + sy - (pass == 0 ? 1 : 0);
            if (px >= 0 && px < sz && py >= 0 && py < sz)
                pixels[py * sz + px] = pass == 0
                    ? new Color(0f, 0f, 0f, 0.55f)
                    : new Color(1f, 1f, 1f, 1f);
        }

        tex.SetPixels(pixels);
        tex.Apply();
        _checkmarkSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        return _checkmarkSprite;
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

    // Couches d'un badge EVO arc-en-ciel à animer (rotation indépendante du ring et du shine).
    internal class AnimBadge { public Transform ring; public Transform shine; }
    internal static readonly List<AnimBadge> _animBadges = new List<AnimBadge>();

    static void AddBadge(GameObject go, string evoName, bool isRedundant = false)
    {
        var tween = GetTweenContainer(go);
        if (tween.Find(BadgeName) != null) return;

        var badgeGo = new GameObject(BadgeName);
        badgeGo.layer = go.layer;
        badgeGo.transform.SetParent(tween, false);
        var badgeRT = badgeGo.AddComponent<RectTransform>();
        badgeRT.anchoredPosition = new Vector2(59f, -17.5f);
        // EVO bis (redondant) : médaillon plus petit pour signaler "info secondaire"
        badgeRT.sizeDelta = isRedundant ? new Vector2(36f, 36f) : new Vector2(48f, 48f);
        badgeGo.transform.SetAsLastSibling();

        // Couche 1 : ring (rainbow ou silver) — tournable indépendamment du texte
        var ringGo = new GameObject("Ring");
        ringGo.layer = go.layer;
        ringGo.transform.SetParent(badgeGo.transform, false);
        var ringRT = ringGo.AddComponent<RectTransform>();
        ringRT.anchorMin = Vector2.zero; ringRT.anchorMax = Vector2.one;
        ringRT.offsetMin = ringRT.offsetMax = Vector2.zero;
        ringGo.AddComponent<Image>().sprite = GetBadgeRingSprite(silver: isRedundant);

        Transform shineRT = null;
        if (!isRedundant)
        {
            // Couche 2 : balayage lumineux superposé (rainbow uniquement)
            var shineGo = new GameObject("Shine");
            shineGo.layer = go.layer;
            shineGo.transform.SetParent(badgeGo.transform, false);
            var shineRect = shineGo.AddComponent<RectTransform>();
            shineRect.anchorMin = Vector2.zero; shineRect.anchorMax = Vector2.one;
            shineRect.offsetMin = shineRect.offsetMax = Vector2.zero;
            shineGo.AddComponent<Image>().sprite = GetShineSprite();
            shineRT = shineGo.transform;
        }

        // Couche 3 : texte EVO blanc (statique, en haut)
        var textGo = new GameObject("Text");
        textGo.layer = go.layer;
        textGo.transform.SetParent(badgeGo.transform, false);
        var textRT = textGo.AddComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = textRT.offsetMax = Vector2.zero;
        textGo.AddComponent<Image>().sprite = GetEvoTextSprite();

        if (isRedundant)
        {
            // Couche 4 (silver only) : pastille verte ✓ en haut-droit
            var checkGo = new GameObject("Check");
            checkGo.layer = go.layer;
            checkGo.transform.SetParent(badgeGo.transform, false);
            var checkRT = checkGo.AddComponent<RectTransform>();
            checkRT.anchorMin = checkRT.anchorMax = new Vector2(1f, 1f);
            checkRT.pivot = new Vector2(0.5f, 0.5f);
            checkRT.anchoredPosition = new Vector2(2f, 2f);
            checkRT.sizeDelta = new Vector2(18f, 18f);
            checkGo.AddComponent<Image>().sprite = GetCheckmarkSprite();
        }
        else
        {
            // Inscription pour animation per-frame
            _animBadges.Add(new AnimBadge { ring = ringGo.transform, shine = shineRT });
        }
    }
}
