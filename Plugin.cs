using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using Nosebleed.Pancake.GameConfig;
using Nosebleed.Pancake.GameLogic;
using Nosebleed.Pancake.Modal;
using Nosebleed.Pancake.Models;
using Nosebleed.Pancake.View;
using Nosebleed.Util;
using TMPro;
using UObject = UnityEngine.Object;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BetterCards;

[BepInPlugin("com.tovak.vc.bettercards", "BetterCards", "1.3.3")]
public class Plugin : BasePlugin
{
    internal static new BepInEx.Logging.ManualLogSource Log;
    internal static string CurrentLangCode = "";
    internal static bool LocaleRebuildPending = false;
    internal static ConfigEntry<bool> CardLockEnabled;

    public override void Load()
    {
        Log = base.Log;

        CardLockEnabled = Config.Bind(
            "CardLock", "Enabled", true,
            "Right-click any card, press numpad '.', or press gamepad Select/Back to lock it (golden padlock). Locked cards can't be played accidentally — useful for keeping Destruction cards alive until fusion.");

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

        // Patch Harmony : capture les changements de langue en temps réel + Card Lock
        try
        {
            var harmony = new HarmonyLib.Harmony("com.tovak.vc.bettercards");
            harmony.PatchAll(typeof(LocalePatch));
            harmony.PatchAll(typeof(PlayerModel_CanAffordCard_Patch));
            harmony.PatchAll(typeof(PlayerModel_AreAnyCardsInHandPlayable_Patch));
            harmony.PatchAll(typeof(PlayerModel_TryPlayCard_Patch));
            harmony.PatchAll(typeof(PlayerModel_SimplePlayCard_Patch));
            harmony.PatchAll(typeof(CardModel_TryEvolveCard_Patch));
            harmony.PatchAll(typeof(PlayerModel_OnCardEvolved_Patch));
            harmony.PatchAll(typeof(PlayableCard_TryPlayCard_Patch));
            harmony.PatchAll(typeof(PlayableCard_OnCardPlayRequested_Patch));
            int patchCount = harmony.GetPatchedMethods().Count();
            Log.LogInfo($"[CardLock] Harmony patches actifs : {patchCount}");
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

// ─── Card Lock : carte verrouillée = traitée comme "non affordable" par le jeu ─
// Postfix sur CanAffordCard qui force false si la carte est verrouillée. Le jeu
// utilise CanAffordCard pour le visuel "carte grisée" (path managé qui passe
// par notre patch).
[HarmonyLib.HarmonyPatch(typeof(PlayerModel), "CanAffordCard")]
public static class PlayerModel_CanAffordCard_Patch
{
    public static void Postfix(CardModel cardModel, ref bool __result)
    {
        try
        {
            if (!__result) return;
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
            if (cardModel != null && ComboObserver.IsLocked(cardModel))
                __result = false;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] CanAffordCard patch err: {ex.Message}"); }
    }
}

// ─── Card Lock : déclenche l'auto-skip de tour si toute la main est verrouillée ─
// AreAnyCardsInHandPlayable est appelée par AutoEndTurnCheck (path natif qui
// peut ne pas hit notre patch CanAffordCard). On force false si la main ne
// contient que des cartes verrouillées ou non-affordables.
[HarmonyLib.HarmonyPatch(typeof(PlayerModel), "AreAnyCardsInHandPlayable")]
public static class PlayerModel_AreAnyCardsInHandPlayable_Patch
{
    public static void Postfix(PlayerModel __instance, ref bool __result)
    {
        try
        {
            if (!__result) return;
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
            if (__instance == null) return;

            // _cards est le List<CardModel> sous-jacent (Cards en IReadOnlyList n'a
            // pas Count en IL2CPP managed). On accède direct au champ.
            var hand = __instance.HandPile?.CardPile?._cards;
            if (hand == null) return;
            int count = hand.Count;
            // Au moins une carte non-verrouillée et affordable → on garde true
            for (int i = 0; i < count; i++)
            {
                var cm = hand[i];
                if (cm == null) continue;
                if (ComboObserver.IsLocked(cm)) continue;
                if (__instance.CanAffordCard(cm)) return; // une carte est jouable
            }
            __result = false;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] AreAnyCardsInHandPlayable patch err: {ex.Message}"); }
    }
}

// ─── Card Lock : bloque le play via clavier (espace) ───────────────────────
// PlayableCard.TryPlayCard est l'entrée souris/drag. PlayerModel.TryPlayCard
// est l'entrée haut-niveau utilisée par le clavier (touche espace) et certains
// effets internes. On patche les deux pour couvrir tous les chemins.
[HarmonyLib.HarmonyPatch(typeof(PlayerModel), "TryPlayCard", new[] { typeof(CardModel), typeof(bool) })]
public static class PlayerModel_TryPlayCard_Patch
{
    private static bool _firstHitLogged = false;
    public static bool Prefix(CardModel cardModel, bool isAutoPlay, ref bool __result)
    {
        try
        {
            if (!_firstHitLogged)
            {
                _firstHitLogged = true;
                var k = ComboObserver.GetLockKey(cardModel);
                Plugin.Log.LogInfo($"[CardLock] PlayerModel.TryPlayCard prefix actif (guid={k ?? "null"}, autoPlay={isAutoPlay}, locked={ComboObserver.IsLocked(cardModel)})");
            }
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return true;
            if (cardModel == null) return true;
            if (!ComboObserver.IsLocked(cardModel)) return true;
            ComboObserver.PlayBlockedFeedback(null);
            __result = false;
            return false;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] PlayerModel.TryPlayCard patch err: {ex.Message}"); return true; }
    }
}

// ─── Card Lock : bloque la lecture d'une carte verrouillée (souris/drag) ───
// PlayableCard.TryPlayCard est l'entrée pour clic/drag. Ajoute le shake natif.
// Postfix : observe les tentatives, déclenche feedback throttle.
[HarmonyLib.HarmonyPatch(typeof(PlayableCard), "TryPlayCard")]
public static class PlayableCard_TryPlayCard_Patch
{
    private static bool _firstFireLogged = false;
    public static void Postfix(PlayableCard __instance)
    {
        try
        {
            var cm = __instance?._cardModel;
            bool locked = ComboObserver.IsLocked(cm);
            if (!_firstFireLogged) { _firstFireLogged = true; Plugin.Log.LogInfo($"[CardLock] TryPlayCard 1st fire (locked={locked})"); }
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
            if (cm == null || !locked) return;
            ComboObserver.PlayBlockedFeedback(__instance);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] TryPlayCard err: {ex.Message}"); }
    }
}

// ─── Card Lock : bloque le play déclenché par sélection clavier (espace/manette) ─
// OnCardPlayRequested est le listener subscribed à InteractableCard.CardSelected.
// Le clavier (espace) sélectionne la carte → CardSelected fire → OnCardPlayRequested
// joue la carte sans repasser par PlayableCard.TryPlayCard.
// Postfix : observe les tentatives, déclenche shake + son sur carte verrouillée.
// Throttle à 300ms pour éviter le stacking (clavier maintenu = ~30 fires/s).
// Pas de "return false" → le flow naturel du jeu décide ce qui se passe.
[HarmonyLib.HarmonyPatch(typeof(PlayableCard), "OnCardPlayRequested")]
public static class PlayableCard_OnCardPlayRequested_Patch
{
    private static bool _firstFireLogged = false;
    public static void Postfix(PlayableCard __instance)
    {
        try
        {
            var cm = __instance?._cardModel;
            bool locked = ComboObserver.IsLocked(cm);
            if (!_firstFireLogged) { _firstFireLogged = true; Plugin.Log.LogInfo($"[CardLock] OnCardPlayRequested 1st fire (locked={locked})"); }
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
            if (cm == null || !locked) return;
            ComboObserver.PlayBlockedFeedback(__instance);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] OnCardPlayRequested err: {ex.Message}"); }
    }
}

// ─── Card Lock : safety net pour les chemins de play directs natifs ─────────
[HarmonyLib.HarmonyPatch(typeof(PlayerModel), "SimplePlayCard")]
public static class PlayerModel_SimplePlayCard_Patch
{
    private static bool _firstHitLogged = false;
    public static bool Prefix(CardModel cardModel)
    {
        try
        {
            if (!_firstHitLogged)
            {
                _firstHitLogged = true;
                Plugin.Log.LogInfo("[CardLock] PlayerModel.SimplePlayCard prefix actif");
            }
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return true;
            if (cardModel == null) return true;
            if (!ComboObserver.IsLocked(cardModel)) return true;
            ComboObserver.PlayBlockedFeedback(null);
            return false;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] SimplePlayCard err: {ex.Message}"); return true; }
    }
}

// ─── Card Lock : une carte évoluée ne doit pas transmettre son verrou ───────
[HarmonyLib.HarmonyPatch(typeof(CardModel), "TryEvolveCard")]
public static class CardModel_TryEvolveCard_Patch
{
    public static void Postfix(CardModel __instance, bool __result)
    {
        try
        {
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
            if (!__result) return;
            ComboObserver.RemoveLockForCard(__instance, "TryEvolveCard");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] TryEvolveCard patch err: {ex.Message}"); }
    }
}

// Après une évolution, le jeu peut reconstruire/remapper des CardModel. On purge
// les verrous orphelins ou ceux dont le GUID pointe maintenant vers une autre config.
[HarmonyLib.HarmonyPatch(typeof(PlayerModel), "OnCardEvolved")]
public static class PlayerModel_OnCardEvolved_Patch
{
    public static void Postfix(PlayerModel __instance)
    {
        try
        {
            if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
            ComboObserver.PruneInvalidOrTransferredLocks(__instance, "OnCardEvolved");
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] OnCardEvolved patch err: {ex.Message}"); }
    }
}

public class ComboObserver : MonoBehaviour
{
    public ComboObserver(IntPtr ptr) : base(ptr) { }

    private bool _wasOpen = false;
    private string _lastCardSignature = "";
    private ChooseCardModal _cachedModal;
    private int _searchCooldown = 0;

    private PlayerModel _cachedPlayerModel;
    private int _lockPruneCooldown = 0;

    public void Awake()
    {
        try { EnsureAssetsLoaded(); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] Awake error: {ex.Message}"); }
    }

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
        // Card Lock : clic droit pour verrouiller, feedback clavier sur carte
        // verrouillée, scan visuel + animations + auto-skip
        HandleRightClickToggle();
        HandleKeyboardLockedFeedback();
        CheckLockIntegrity();
        ScanAndUpdateLockBadges();
        TickLockPopAnims();
        CheckAutoEndTurn();

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
                _cachedModal = null;
            }
        }
        _wasOpen = isOpen;
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

    // Index global de tous les noms de cartes connus (normalisés). Utilisé pour
    // détecter les composants "orphelins" : un comp dont le nom ne correspond à
    // AUCUNE carte de la DB du jeu (typo / mismatch interne, ex. "Tirimasu" dans
    // la recette alors que la carte s'appelle "Tirajisu").
    static HashSet<string> _allKnownNorms;
    // Norms des noms de cartes qui sont RÉFÉRENCÉS comme composant d'au moins une recette
    // d'évolution. Sert à filtrer les candidats orphelins : si une carte apparait comme
    // comp dans une autre recette, elle a son propre rôle → ne pas l'utiliser pour combler
    // un orphelin d'une recette différente.
    static HashSet<string> _allCompRefs;

    public static void InvalidateCardIndices()
    {
        _allKnownNorms = null;
        _allCompRefs = null;
    }

    static void EnsureCardIndices()
    {
        if (_allKnownNorms != null && _allCompRefs != null) return;
        var known = new HashSet<string>(StringComparer.Ordinal);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var arr = Resources.FindObjectsOfTypeAll<CardConfig>();
            foreach (var c in arr)
            {
                if (c == null) continue;
                if (!string.IsNullOrEmpty(c.name))
                {
                    var n1 = GetBaseName(c.name);
                    if (n1.Length > 0) known.Add(n1);
                }
                try
                {
                    var disp = c.Name;
                    if (!string.IsNullOrEmpty(disp))
                    {
                        var n2 = Norm(disp);
                        if (n2.Length > 0) known.Add(n2);
                    }
                }
                catch { }

                try
                {
                    if (c.HasEvolution)
                    {
                        var d = c.GetEvolveRequirementDescription();
                        if (!string.IsNullOrEmpty(d))
                        {
                            var comps = ParseComponents(d);
                            foreach (var comp in comps)
                            {
                                var n = Norm(comp);
                                if (n.Length > 0) refs.Add(n);
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[ComboIndicator] EnsureCardIndices: {ex.Message}"); }
        _allKnownNorms = known;
        _allCompRefs = refs;
    }
    static bool IsKnownCompName(string norm) => norm.Length > 0 && _allKnownNorms != null && _allKnownNorms.Contains(norm);
    static bool IsCardReferencedAsComp(CardConfig c)
    {
        if (c == null || _allCompRefs == null) return false;
        if (!string.IsNullOrEmpty(c.name))
        {
            var n1 = GetBaseName(c.name);
            if (n1.Length > 0 && _allCompRefs.Contains(n1)) return true;
        }
        try
        {
            var disp = c.Name;
            if (!string.IsNullOrEmpty(disp))
            {
                var n2 = Norm(disp);
                if (n2.Length > 0 && _allCompRefs.Contains(n2)) return true;
            }
        }
        catch { }
        return false;
    }

    // Tente d'assigner chaque composant à une carte unique du pool.
    // Pass 1 : matching texte greedy. Si un comp ne matche aucune carte du pool ET
    //          qu'il est connu ailleurs dans la DB, c'est un échec définitif.
    // Pass 2 : les comps "orphelins" (= nom inconnu, ex. "Tirimasu" qui n'existe
    //          dans aucun asset/displayname) consomment chacun une carte non-utilisée
    //          qui : (a) ne text-match aucun autre comp et (b) pointe vers la même
    //          évolution cible (targetEvolved). Cette dernière condition est le vrai
    //          garde-fou : sans elle n'importe quelle carte offerte serait acceptée
    //          comme remplissant l'orphelin (faux positifs massifs).
    // Au moins UN comp doit avoir été texte-matché en pass 1.
    static HashSet<CardConfig> TryAssignComps(string[] comps, List<CardConfig> pool, CardConfig targetEvolved)
    {
        var used = new HashSet<CardConfig>();
        var orphanIndices = new List<int>();
        int textMatched = 0;
        for (int i = 0; i < comps.Length; i++)
        {
            var n = Norm(comps[i]);
            if (n.Length == 0) continue;
            CardConfig found = null;
            foreach (var c in pool)
            {
                if (c == null || used.Contains(c)) continue;
                if (CardMatchesComp(c, n)) { found = c; break; }
            }
            if (found != null) { used.Add(found); textMatched++; continue; }
            if (IsKnownCompName(n)) return null;
            orphanIndices.Add(i);
        }
        if (orphanIndices.Count > 0 && textMatched == 0) return null;
        if (orphanIndices.Count > 0 && targetEvolved == null) return null;
        foreach (var idx in orphanIndices)
        {
            var orphanNorm = Norm(comps[idx]);
            CardConfig found = null;
            foreach (var c in pool)
            {
                if (c == null || used.Contains(c)) continue;
                bool conflicts = false;
                for (int i = 0; i < comps.Length; i++)
                {
                    var n = Norm(comps[i]);
                    if (n.Length > 0 && CardMatchesComp(c, n)) { conflicts = true; break; }
                }
                if (conflicts) continue;
                // Candidate valide pour orphelin si :
                //  (a) elle pointe vers la même évolution cible (cas standard), OU
                //  (b) elle correspond à une exception connue et bornée. Le fallback large
                //      "n'importe quelle carte sans évolution" créait des faux EVO sur
                //      certains objets depuis la 1.5 (ex. Os / Bombe flamboyante).
                bool ok = false;
                if (SameEvolutionTarget(c, targetEvolved)) ok = true;
                else if (IsAllowedOrphanComponentFallback(c, orphanNorm)) ok = true;
                if (!ok) continue;
                found = c; break;
            }
            if (found == null) return null;
            used.Add(found);
        }
        return used;
    }

    // Retourne true si la carte 'c' pointe vers la même évolution finale que 'target'.
    // Deux cartes "ingrédients" d'une même fusion ont leur EvolvedCardConfig qui pointe
    // vers la même carte évoluée (ex. Phiera.EvolvedCardConfig == Eight.EvolvedCardConfig == Phieraggi).
    static bool SameEvolutionTarget(CardConfig c, CardConfig target)
    {
        if (c == null || target == null) return false;
        try
        {
            if (!c.HasEvolution) return false;
            var ce = c.EvolvedCardConfig;
            if (ce == null) return false;
            if (ReferenceEquals(ce, target)) return true;
            return ce.name == target.name;
        }
        catch { return false; }
    }

    static bool IsAllowedOrphanComponentFallback(CardConfig c, string orphanNorm)
    {
        if (c == null || string.IsNullOrEmpty(orphanNorm)) return false;
        if (orphanNorm != "tirimasu" && orphanNorm != "tiramisu") return false;

        try { if (c.HasEvolution) return false; } catch { }

        if (!string.IsNullOrEmpty(c.name))
        {
            var n = GetBaseName(c.name);
            if (n == "tirajisu" || n == "tiragisu") return true;
        }

        try
        {
            var display = c.Name;
            if (!string.IsNullOrEmpty(display))
            {
                var n = Norm(display);
                if (n == "tirajisu" || n == "tiragisu") return true;
            }
        }
        catch { }

        return false;
    }

    // Retourne (hasCombo, evoName, isRedundant). isRedundant = true si tous les composants
    // sont déjà dans le deck sans avoir besoin de piocher la carte offerte (combo couvert).
    static (bool hasCombo, string evoName, bool isRedundant) CheckCombo(CardConfig offered, List<CardConfig> ownedConfigs)
    {
        EnsureCardIndices();

        // Case 1 : la carte offerte peut évoluer si le joueur possède tous les autres composants
        if (offered.HasEvolution)
        {
            var rawDesc = offered.GetEvolveRequirementDescription();
            var comps = ParseComponents(rawDesc);
            if (comps.Length > 0)
            {
                var pool = new List<CardConfig>(ownedConfigs);
                if (!pool.Contains(offered)) pool.Add(offered);
                var used = TryAssignComps(comps, pool, offered.EvolvedCardConfig);
                if (used != null)
                {
                    bool anyFromDeck = used.Any(c => !ReferenceEquals(c, offered) && ownedConfigs.Contains(c));
                    if (anyFromDeck)
                    {
                        bool allInDeck = used.All(c => ownedConfigs.Contains(c));
                        var evolved = offered.EvolvedCardConfig;
                        return (true, evolved?.Name ?? "?", allInDeck);
                    }
                }
            }
        }

        // Case 2 : la carte offerte complète le combo d'une carte déjà possédée
        foreach (var owned in ownedConfigs)
        {
            if (owned == null || !owned.HasEvolution) continue;
            var rawDesc = owned.GetEvolveRequirementDescription();
            var comps = ParseComponents(rawDesc);
            if (comps.Length == 0) continue;

            var pool = new List<CardConfig> { offered };
            foreach (var c in ownedConfigs) pool.Add(c);
            var used = TryAssignComps(comps, pool, owned.EvolvedCardConfig);
            if (used == null) continue;
            if (!used.Contains(offered)) continue;

            bool allInDeck = used.All(c => ownedConfigs.Contains(c));
            var evolved = owned.EvolvedCardConfig;
            return (true, evolved?.Name ?? "?", allInDeck);
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

    // ─── Card Lock ────────────────────────────────────────────────────────────
    // État de verrou par carte : on stocke le GUID stable de chaque CardModel
    // verrouillé. SerializableGuid → System.Guid → string pour avoir une clé
    // hashable côté managed (les structs Il2Cpp ne s'utilisent pas bien dans
    // un HashSet C#).
    internal static readonly HashSet<string> _lockedCardKeys = new HashSet<string>();
    internal static readonly Dictionary<string, string> _lockedKeyConfigs = new Dictionary<string, string>();
    const string LockBadgeName = "BC_LockBadge";
    private int _lockBadgeScanCooldown = 0;
    private static Sprite _lockBadgeSprite;

    // ─── Card Lock — assets embarqués (PNG cadenas + 3 WAV) ──────────────────
    private static AudioSource _audioSource;
    private static AudioClip _clipLock, _clipUnlock, _clipBlocked;
    private static bool _assetsLoaded = false;

    static byte[] LoadResourceBytes(string name)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // Charge un sprite depuis des bytes RGBA bruts (pré-décodés à la compilation).
    // ImageConversion.LoadImage va chercher ReadOnlySpan.GetPinnableReference qui
    // n'existe pas en IL2CPP, alors on contourne en utilisant LoadRawTextureData
    // qui passe directement par le runtime sans intermédiaire Span.
    static Sprite LoadEmbeddedRawRgba(string resourceName, int width, int height)
    {
        try
        {
            var bytes = LoadResourceBytes(resourceName);
            if (bytes == null) { Plugin.Log.LogWarning($"[CardLock] resource null: {resourceName}"); return null; }
            int expected = width * height * 4;
            if (bytes.Length != expected)
            {
                Plugin.Log.LogWarning($"[CardLock] taille rgba {bytes.Length} != attendue {expected}");
                return null;
            }
            var il2cppBytes = new Il2CppStructArray<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) il2cppBytes[i] = bytes[i];

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point; // pixel-art crisp
            tex.hideFlags = HideFlags.HideAndDontSave; // empêche Resources.UnloadUnusedAssets de virer la texture
            tex.LoadRawTextureData(il2cppBytes);
            tex.Apply();
            Plugin.Log.LogInfo($"[CardLock] sprite chargé : {tex.width}x{tex.height}");
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            sp.hideFlags = HideFlags.HideAndDontSave;
            return sp;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] LoadEmbeddedRawRgba error: {ex.Message}"); return null; }
    }

    // Parse un WAV PCM 16-bit (mono ou stéréo) → AudioClip Unity. Suit la spec
    // RIFF en sautant les sous-chunks jusqu'à "data". Pas de support 8/24/32-bit
    // ni compression : on contrôle le format à l'export, donc 16-bit suffit.
    static AudioClip LoadEmbeddedWav(string resourceName, string clipName)
    {
        try
        {
            var wav = LoadResourceBytes(resourceName);
            if (wav == null || wav.Length < 44) return null;
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;

            int channels = wav[22] | (wav[23] << 8);
            int sampleRate = wav[24] | (wav[25] << 8) | (wav[26] << 16) | (wav[27] << 24);
            int bitsPerSample = wav[34] | (wav[35] << 8);
            if (bitsPerSample != 16)
            {
                Plugin.Log.LogWarning($"[CardLock] WAV {clipName} : {bitsPerSample}-bit non supporté");
                return null;
            }

            // Trouver le chunk "data" (peut être après "fmt " et d'autres chunks optionnels)
            int idx = 12;
            int dataOffset = -1, dataSize = 0;
            while (idx + 8 <= wav.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(wav, idx, 4);
                int chunkSize = wav[idx + 4] | (wav[idx + 5] << 8) | (wav[idx + 6] << 16) | (wav[idx + 7] << 24);
                if (chunkId == "data") { dataOffset = idx + 8; dataSize = chunkSize; break; }
                idx += 8 + chunkSize;
            }
            if (dataOffset < 0) return null;

            int totalShorts = dataSize / 2;
            int samplesPerChannel = totalShorts / channels;
            // Il2CppStructArray pour passer un tableau float au binding IL2CPP de SetData
            var samples = new Il2CppStructArray<float>(totalShorts);
            for (int i = 0; i < totalShorts; i++)
            {
                short s = (short)(wav[dataOffset + i * 2] | (wav[dataOffset + i * 2 + 1] << 8));
                samples[i] = s / 32768f;
            }

            var clip = AudioClip.Create(clipName, samplesPerChannel, channels, sampleRate, false);
            clip.SetData(samples, 0);
            clip.hideFlags = HideFlags.HideAndDontSave; // évite la purge par Resources.UnloadUnusedAssets sur transition de scène
            return clip;
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] LoadEmbeddedWav {clipName} error: {ex.Message}"); return null; }
    }

    void EnsureAssetsLoaded()
    {
        if (_assetsLoaded) return;
        _assetsLoaded = true;

        // Le RootNamespace du csproj est "BetterCards", donc l'asset à
        // <projet>/assets/foo.bar devient la ressource "BetterCards.assets.foo.bar".
        // padlock.rgba = padlock.png pré-décodé en raw bytes 128x128 RGBA bottom-up.
        _lockBadgeSprite = LoadEmbeddedRawRgba("BetterCards.assets.padlock.rgba", 128, 128);
        if (_lockBadgeSprite == null)
            Plugin.Log.LogWarning("[CardLock] padlock.rgba introuvable, fallback procédural");

        _clipLock    = LoadEmbeddedWav("BetterCards.assets.lock.wav",    "bc_lock");
        _clipUnlock  = LoadEmbeddedWav("BetterCards.assets.unlock.wav",  "bc_unlock");
        _clipBlocked = LoadEmbeddedWav("BetterCards.assets.blocked.wav", "bc_blocked");

        if (_audioSource == null)
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;       // 2D / UI : pas de spatialisation
            _audioSource.volume = 1f;
            _audioSource.bypassListenerEffects = true;
            _audioSource.bypassEffects = true;
        }

        LoadLocks();

        Plugin.Log.LogInfo($"[CardLock] assets : sprite={_lockBadgeSprite != null}, lock={_clipLock != null}, unlock={_clipUnlock != null}, blocked={_clipBlocked != null}");
        if (_clipLock != null) Plugin.Log.LogInfo($"[CardLock] clip lock : {_clipLock.length:F2}s, {_clipLock.samples} samples, {_clipLock.channels}ch, {_clipLock.frequency}Hz");
        if (_clipUnlock != null) Plugin.Log.LogInfo($"[CardLock] clip unlock : {_clipUnlock.length:F2}s, {_clipUnlock.samples} samples");
        if (_clipBlocked != null) Plugin.Log.LogInfo($"[CardLock] clip blocked : {_clipBlocked.length:F2}s, {_clipBlocked.samples} samples");
    }

    // Disque doré procédural pour servir de fond derrière le cadenas (effet "médaillon")
    private static Sprite _goldenDiscSprite;
    // Liseré doré seul (intérieur transparent) pour servir d'anneau autour du
    // cadenas, posé sur l'orbe de mana de la carte. La carte voit son orbe par
    // transparence à travers l'anneau.
    static Sprite GetGoldenDiscSprite()
    {
        if (_goldenDiscSprite != null) return _goldenDiscSprite;
        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[sz * sz];

        Color outline = new Color(0.10f, 0.07f, 0.02f, 1f);    // contour fin (extérieur + intérieur)
        Color goldOuter = new Color(0.55f, 0.36f, 0.05f, 1f);  // bord doré sombre
        Color goldRim = new Color(1.00f, 0.78f, 0.20f, 1f);    // anneau doré brillant

        float cx = sz / 2f, cy = sz / 2f;
        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            Color col = Color.clear;

            // Anneau (6px sur 64px) : contour ext 1px + bande dorée brillante
            // 4px + contour int 1px. Étendu vers l'intérieur (extérieur fixe).
            if (dist >= 25f && dist <= 31f)
            {
                if (dist >= 30f) col = outline;       // bord extérieur 1px (30-31)
                else if (dist >= 26f) col = goldRim;  // bande dorée brillante 4px (26-30)
                else col = outline;                    // bord intérieur 1px (25-26)
            }
            pixels[y * sz + x] = col;
        }
        tex.SetPixels(pixels);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        _goldenDiscSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        _goldenDiscSprite.hideFlags = HideFlags.HideAndDontSave;
        return _goldenDiscSprite;
    }

    static void PlayClip(AudioClip clip)
    {
        if (clip == null || _audioSource == null) return;
        try { _audioSource.PlayOneShot(clip); }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] PlayOneShot failed : {ex.Message}"); }
    }

    // Clé GUID (per-instance). En combat les CardModel ont leur vrai GUID
    // donc lock par GUID = par carte. Dans les modals le GUID est partagé
    // → on a une logique fallback par CardConfig.name pour ces cas-là.
    internal static string GetLockKey(CardModel cm)
    {
        if (cm == null) return null;
        try
        {
            var s = cm.Guid.ToGuid().ToString();
            if (string.IsNullOrEmpty(s) || s == "00000000-0000-0000-0000-000000000000") return null;
            return s;
        }
        catch { return null; }
    }

    // Détecte si une carte est de type WILD via son CardConfig.name (pattern
    // stable, pas affecté par les modifs de combo). Plugin.cs utilise déjà
    // cette heuristique : 3 segments OU 2e segment == "E".
    static bool IsWildCard(CardModel cm)
    {
        var cfg = GetCardConfigName(cm);
        if (cfg == null) return false;
        var np = cfg.Split('_');
        if (np.Length == 3) return true;
        if (np.Length >= 2 && np[1].ToUpperInvariant() == "E") return true;
        return false;
    }

    static string GetCardConfigName(CardModel cm)
    {
        if (cm == null) return null;
        try
        {
            string n = null;
            try { n = cm.CardView?._appliedCardConfig?.name; } catch { }
            if (string.IsNullOrEmpty(n)) try { n = cm.CardConfig?.name; } catch { }
            if (string.IsNullOrEmpty(n)) return null;
            int cloneIdx = n.IndexOf("(Clone)", System.StringComparison.Ordinal);
            if (cloneIdx > 0) n = n.Substring(0, cloneIdx).TrimEnd();
            return n;
        }
        catch { return null; }
    }

    // Cache des configs ayant au moins une carte lockée. Recalculé à la
    // demande quand les locks changent. Évite l'iteration de _allCards à
    // chaque check IsLockedByConfig (appelé une fois par cscv par scan).
    internal static readonly HashSet<string> _lockedConfigsCache = new HashSet<string>();
    internal static bool _lockedConfigsCacheDirty = true;

    static void RebuildLockedConfigsCache(PlayerModel pm)
    {
        _lockedConfigsCache.Clear();
        if (pm == null) return;
        var allCards = pm._allCards;
        if (allCards == null) return;
        foreach (var rcm in allCards)
        {
            if (rcm == null) continue;
            var rk = GetLockKey(rcm);
            if (rk == null || !_lockedCardKeys.Contains(rk)) continue;
            var cfg = GetCardConfigName(rcm);
            if (cfg != null)
            {
                _lockedConfigsCache.Add(cfg);
                if (!_lockedKeyConfigs.ContainsKey(rk))
                    _lockedKeyConfigs[rk] = cfg;
            }
        }
        _lockedConfigsCacheDirty = false;
    }

    // IsLockedByConfig : O(1) via cache. Reconstruit si dirty.
    internal static bool IsLockedByConfig(CardModel cm, PlayerModel pm)
    {
        if (cm == null) return false;
        var k = GetLockKey(cm);
        if (k != null && _lockedCardKeys.Contains(k)) return true;
        if (_lockedConfigsCacheDirty) RebuildLockedConfigsCache(pm);
        var cfg = GetCardConfigName(cm);
        return cfg != null && _lockedConfigsCache.Contains(cfg);
    }

    // Trouve le rang de ce cscv parmi ses siblings ayant le même CardConfig.
    // Utilisé pour mapper "la Nème Tome vierge du modal" à "la Nème Tome
    // vierge des cartes du joueur" → distingue les copies.
    static int GetCscvRank(CardSelectionCardView cscv, string cfg)
    {
        if (cscv == null || cfg == null) return -1;
        var parent = cscv.transform.parent;
        if (parent == null) return -1;
        int rank = 0;
        int childCount = parent.childCount;
        for (int i = 0; i < childCount; i++)
        {
            var child = parent.GetChild(i);
            CardSelectionCardView c = null;
            try { c = child.GetComponent<CardSelectionCardView>(); } catch { }
            if (c == null) continue;
            CardModel ccm = null;
            try { ccm = c.CardModel?.TryCast<CardModel>(); } catch { }
            if (ccm == null) continue;
            if (GetCardConfigName(ccm) != cfg) continue;
            if (c == cscv) return rank;
            rank++;
        }
        return -1;
    }

    // Trouve la Nème carte réelle de _allCards ayant le même CardConfig.
    // On trie par GUID pour garantir un ordre déterministe entre lancements.
    static readonly List<string> _matchingGuidsBuf = new List<string>();
    static string FindRealGuidForRank(PlayerModel pm, string cfg, int rank)
    {
        if (pm == null || cfg == null || rank < 0) return null;
        var allCards = pm._allCards;
        if (allCards == null) return null;
        _matchingGuidsBuf.Clear();
        foreach (var rcm in allCards)
        {
            if (rcm == null) continue;
            if (GetCardConfigName(rcm) != cfg) continue;
            var rk = GetLockKey(rcm);
            if (rk != null) _matchingGuidsBuf.Add(rk);
        }
        if (rank >= _matchingGuidsBuf.Count) return null;
        _matchingGuidsBuf.Sort(StringComparer.Ordinal);
        return _matchingGuidsBuf[rank];
    }

    // Per-instance lock state pour un cscv via le mapping rank-based
    static bool IsCscvRealCardLocked(CardSelectionCardView cscv, PlayerModel pm)
    {
        if (cscv == null) return false;
        CardModel cm = null;
        try { cm = cscv.CardModel?.TryCast<CardModel>(); } catch { }
        if (cm == null) return false;
        var cfg = GetCardConfigName(cm);
        if (cfg == null) return false;
        int rank = GetCscvRank(cscv, cfg);
        if (rank < 0) return false;
        var realGuid = FindRealGuidForRank(pm, cfg, rank);
        return realGuid != null && _lockedCardKeys.Contains(realGuid);
    }

    internal static bool IsLocked(CardModel cm)
    {
        var k = GetLockKey(cm);
        return k != null && _lockedCardKeys.Contains(k);
    }

    internal static void ToggleLock(CardModel cm)
    {
        var k = GetLockKey(cm);
        if (k == null) return;
        if (_lockedCardKeys.Contains(k))
        {
            _lockedCardKeys.Remove(k);
            _lockedKeyConfigs.Remove(k);
            Plugin.Log.LogInfo($"[CardLock] unlocked {k}");
            PlayClip(_clipUnlock);
        }
        else
        {
            _lockedCardKeys.Add(k);
            var cfg = GetCardConfigName(cm);
            if (cfg != null) _lockedKeyConfigs[k] = cfg;
            Plugin.Log.LogInfo($"[CardLock] locked {k}");
            PlayClip(_clipLock);
        }
        _lockedConfigsCacheDirty = true;
        SaveLocks();
        TriggerAutoEndTurnCheck();
    }

    // Toggle depuis un cscv de modal : per-instance via mapping rank-based.
    // Le cscv N de type X dans le modal correspond à la Nème carte réelle de
    // type X dans _allCards (triée par GUID). Lock UNIQUEMENT cette copie-là.
    internal static void ToggleLockFromCSCV(CardSelectionCardView cscv)
    {
        if (cscv == null) return;
        var icm = cscv.CardModel;
        var cm = icm?.TryCast<CardModel>();
        if (cm == null) return;
        var cfg = GetCardConfigName(cm);
        if (cfg == null) return;

        var pm = UObject.FindObjectOfType<PlayerModel>();
        if (pm == null) return;

        int rank = GetCscvRank(cscv, cfg);
        if (rank < 0) return;
        var realGuid = FindRealGuidForRank(pm, cfg, rank);
        if (realGuid == null) return;

        if (_lockedCardKeys.Contains(realGuid))
        {
            _lockedCardKeys.Remove(realGuid);
            _lockedKeyConfigs.Remove(realGuid);
            Plugin.Log.LogInfo($"[CardLock] modal unlock {realGuid} (cfg={cfg} rank={rank})");
            PlayClip(_clipUnlock);
        }
        else
        {
            _lockedCardKeys.Add(realGuid);
            _lockedKeyConfigs[realGuid] = cfg;
            Plugin.Log.LogInfo($"[CardLock] modal lock {realGuid} (cfg={cfg} rank={rank})");
            PlayClip(_clipLock);
        }
        _lockedConfigsCacheDirty = true;
        SaveLocks();
        TriggerAutoEndTurnCheck();
    }

    internal static void RemoveLockForCard(CardModel cm, string reason)
    {
        var k = GetLockKey(cm);
        if (k == null || !_lockedCardKeys.Contains(k)) return;
        var cfg = GetCardConfigName(cm) ?? "unknown";
        _lockedCardKeys.Remove(k);
        _lockedKeyConfigs.Remove(k);
        _lockedConfigsCacheDirty = true;
        SaveLocks();
        Plugin.Log.LogInfo($"[CardLock] lock removed before evolution ({reason}) : {k} cfg={cfg}");
    }

    internal static void PruneInvalidOrTransferredLocks(PlayerModel pm, string reason)
    {
        try
        {
            if (_lockedCardKeys.Count == 0) return;
            if (pm == null) pm = UObject.FindObjectOfType<PlayerModel>();
            var allCards = pm?._allCards;
            if (allCards == null) return;

            var currentConfigs = new Dictionary<string, string>();
            foreach (var cm in allCards)
            {
                if (cm == null) continue;
                var k = GetLockKey(cm);
                if (k == null) continue;
                var cfg = GetCardConfigName(cm);
                if (cfg != null) currentConfigs[k] = cfg;
            }

            int removed = 0, hydrated = 0;
            foreach (var key in _lockedCardKeys.ToArray())
            {
                if (!currentConfigs.TryGetValue(key, out var currentCfg))
                {
                    _lockedCardKeys.Remove(key);
                    _lockedKeyConfigs.Remove(key);
                    removed++;
                    continue;
                }

                if (_lockedKeyConfigs.TryGetValue(key, out var originalCfg) && !string.IsNullOrEmpty(originalCfg))
                {
                    if (!string.IsNullOrEmpty(currentCfg) && currentCfg != originalCfg)
                    {
                        _lockedCardKeys.Remove(key);
                        _lockedKeyConfigs.Remove(key);
                        removed++;
                    }
                }
                else if (!string.IsNullOrEmpty(currentCfg))
                {
                    _lockedKeyConfigs[key] = currentCfg;
                    hydrated++;
                }
            }

            if (removed > 0)
            {
                _lockedConfigsCacheDirty = true;
                SaveLocks();
                Plugin.Log.LogInfo($"[CardLock] {removed} verrou(s) purgé(s) ({reason})");
            }
            else if (hydrated > 0)
            {
                Plugin.Log.LogInfo($"[CardLock] {hydrated} verrou(s) associé(s) à leur config ({reason})");
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] prune locks failed ({reason}) : {ex.Message}"); }
    }

    // Persistance des verrous : un GUID par ligne dans un .txt à côté de la
    // config BepInEx. Les GUIDs des cartes étant stables entre sessions
    // (stockés dans le save), les locks survivent aux redémarrages du jeu et
    // ne s'appliquent qu'aux cartes qui existent encore dans le deck du joueur.
    static string GetLocksFilePath()
    {
        return Path.Combine(BepInEx.Paths.ConfigPath, "BetterCards.locks.txt");
    }

    static void SaveLocks()
    {
        try
        {
            File.WriteAllLines(GetLocksFilePath(), _lockedCardKeys);
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] sauvegarde locks failed: {ex.Message}"); }
    }

    static bool LooksLikeGuid(string s)
    {
        return s != null && s.Length == 36 && s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-';
    }

    static void LoadLocks()
    {
        try
        {
            string path = GetLocksFilePath();
            if (!File.Exists(path)) return;
            int count = 0, skipped = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                // Le format de stockage est GUID (per-instance). Les anciennes
                // entrées format CardConfig.name (ex "Card_A_2_LightningRing")
                // sont ignorées et le fichier est réécrit propre.
                if (!LooksLikeGuid(t)) { skipped++; continue; }
                if (_lockedCardKeys.Add(t)) count++;
            }
            _lockedConfigsCacheDirty = true;
            if (count > 0) Plugin.Log.LogInfo($"[CardLock] {count} verrous restaurés depuis disque");
            if (skipped > 0)
            {
                Plugin.Log.LogInfo($"[CardLock] {skipped} entrées non-GUID ignorées (migration), réécriture du fichier");
                SaveLocks();
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] chargement locks failed: {ex.Message}"); }
    }

    static void TriggerAutoEndTurnCheck()
    {
        try
        {
            var pm = UObject.FindObjectOfType<PlayerModel>();
            pm?.AutoEndTurnCheck();
        }
        catch { }
    }

    void CheckLockIntegrity()
    {
        if (--_lockPruneCooldown > 0) return;
        _lockPruneCooldown = 120;
        if (_lockedCardKeys.Count == 0) return;
        if (_cachedPlayerModel == null) _cachedPlayerModel = UObject.FindObjectOfType<PlayerModel>();
        PruneInvalidOrTransferredLocks(_cachedPlayerModel, "periodic");
    }

    // Polling auto-skip : si toute la main est verrouillée + non-jouable et que
    // le jeu n'a pas pris l'initiative de skipper le tour, on force Button_EndTurn.
    // Bypass propre du fait que AutoEndTurnCheck natif peut ne pas hit notre
    // patch CanAffordCard (chemin natif → managed jamais traversé).
    private int _autoEndPollCooldown = 0;
    private bool _autoEndDone = false;

    void CheckAutoEndTurn()
    {
        if (--_autoEndPollCooldown > 0) return;
        _autoEndPollCooldown = 30; // ~ toutes les 0.5s
        if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;

        if (_cachedPlayerModel == null) _cachedPlayerModel = UObject.FindObjectOfType<PlayerModel>();
        var pm = _cachedPlayerModel;
        if (pm == null) return;

        var hand = pm.HandPile?.CardPile?._cards;
        if (hand == null || hand.Count == 0) { _autoEndDone = false; return; }

        int count = hand.Count;
        bool anyPlayable = false;
        bool anyLocked = false;
        for (int i = 0; i < count; i++)
        {
            var cm = hand[i];
            if (cm == null) continue;
            if (IsLocked(cm)) { anyLocked = true; continue; }
            try { if (pm.CanAffordCard(cm)) { anyPlayable = true; break; } } catch { }
        }

        // Reset le flag si la main contient une carte jouable (nouvelle main, mana ajoutée…)
        if (anyPlayable) { _autoEndDone = false; return; }

        // Trigger l'auto-end-turn une seule fois quand la condition se présente,
        // et uniquement si AU MOINS une carte est verrouillée (ne pas interférer
        // avec les tours normaux où simplement pas assez de mana).
        if (!_autoEndDone && anyLocked)
        {
            _autoEndDone = true;
            Plugin.Log.LogInfo("[CardLock] auto-skip déclenché : toute la main est verrouillée ou non-affordable");
            try { pm.Button_EndTurn(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] Button_EndTurn err: {ex.Message}"); }
        }
    }

    // Throttle court (30ms) : suffit à supprimer les doublons d'une même frame
    // (TryPlayCard postfix + OnCardPlayRequested postfix peuvent fire ensemble
    // en cascade pour un seul clic). Pas un vrai throttle pour gêner le spam.
    private static float _lastBlockedFeedbackTime = -10f;
    internal static void PlayBlockedFeedback(PlayableCard pc)
    {
        float now = Time.unscaledTime;
        if (now - _lastBlockedFeedbackTime < 0.03f) return;
        _lastBlockedFeedbackTime = now;
        if (pc != null) { try { pc.PlayInvalidCardShake(); } catch { } }
        PlayClip(_clipBlocked);
    }

    // Détection clavier : le jeu silently-aborte quand on appuie espace/entrée
    // sur une carte non-affordable (notre CanAffordCard postfix la rend
    // non-affordable). Du coup les patches Harmony ne firent jamais sur
    // espace pour une carte verrouillée. On compense en détectant le press
    // directement dans Update et en triggant le feedback manuellement.
    private static bool _prevSpaceP, _prevEnterP, _prevNumEnterP;

    void HandleKeyboardLockedFeedback()
    {
        if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
        bool edgePress = false;
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool spaceNow = kb.spaceKey.isPressed;
            bool enterNow = kb.enterKey.isPressed;
            bool numEnterNow = kb.numpadEnterKey.isPressed;
            if ((spaceNow && !_prevSpaceP) || (enterNow && !_prevEnterP) || (numEnterNow && !_prevNumEnterP))
                edgePress = true;
            _prevSpaceP = spaceNow;
            _prevEnterP = enterNow;
            _prevNumEnterP = numEnterNow;
        }
        catch { return; }
        if (!edgePress) return;

        // Trouve la carte hover/sélectionnée et vérifie si elle est verrouillée
        try
        {
            var ics = UObject.FindObjectsOfType<InteractableCard>();
            if (ics == null) return;
            foreach (var ic in ics)
            {
                try
                {
                    if (ic == null) continue;
                    bool hovered = false;
                    try { hovered = ic.IsHovering || ic.IsSelected; } catch { }
                    if (!hovered) continue;
                    var cv = ic._cardView;
                    var cm = cv?._cardModel;
                    if (cm == null || !IsLocked(cm)) return; // carte trouvée mais pas lockée → rien à faire
                    // Carte lockée : fire le feedback. PlayableCard peut être sur le même GO,
                    // un parent, un enfant, ou ailleurs (lookup via _interactableCard en dernier).
                    PlayableCard pc = null;
                    try { pc = ic.gameObject.GetComponent<PlayableCard>(); } catch { }
                    if (pc == null) try { pc = ic.gameObject.GetComponentInParent<PlayableCard>(); } catch { }
                    if (pc == null) try { pc = ic.gameObject.GetComponentInChildren<PlayableCard>(); } catch { }
                    if (pc == null)
                    {
                        try
                        {
                            var pcs = UObject.FindObjectsOfType<PlayableCard>();
                            if (pcs != null) foreach (var p in pcs)
                            {
                                try { if (p != null && p._interactableCard == ic) { pc = p; break; } } catch { }
                            }
                        }
                        catch { }
                    }
                    PlayBlockedFeedback(pc);
                    return;
                }
                catch { }
            }
        }
        catch { }
    }

    static bool IsPointerOverCscv(CardSelectionCardView cscv, Vector2 screenPos)
    {
        if (cscv == null) return false;
        try
        {
            var rt = cscv.GetComponent<RectTransform>();
            if (rt == null) return false;

            Camera cam = null;
            try
            {
                var canvas = cscv.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    cam = canvas.worldCamera;
            }
            catch { }

            return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, cam);
        }
        catch { return false; }
    }

    static CardSelectionCardView GetSelectedCscv()
    {
        try
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return null;

            var cscv = go.GetComponent<CardSelectionCardView>();
            if (cscv != null) return cscv;

            cscv = go.GetComponentInParent<CardSelectionCardView>();
            if (cscv != null) return cscv;

            return go.GetComponentInChildren<CardSelectionCardView>();
        }
        catch { return null; }
    }

    // Détecte le clic droit, la touche pavé numérique "." (suppr), ou le
    // bouton Select/Back manette sur une carte en jeu (combat ou modal) et
    // toggle son verrou. Les modals n'utilisent pas CardSelectionCardView._isHovered :
    // la 1.5 a retiré ce getter, donc on passe par le RectTransform / EventSystem.
    void HandleRightClickToggle()
    {
        if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;
        // VC utilise le nouveau Input System (UnityEngine.Input legacy throw).
        bool triggered = false;
        bool allowSelectedFallback = false;
        bool hasPointerPos = false;
        Vector2 pointerPos = Vector2.zero;
        try
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                pointerPos = mouse.position.ReadValue();
                hasPointerPos = true;
                if (mouse.rightButton.wasPressedThisFrame) triggered = true;
            }
        }
        catch { }
        if (!triggered)
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null && kb.numpadPeriodKey.wasPressedThisFrame)
                {
                    triggered = true;
                    allowSelectedFallback = true;
                }
            }
            catch { }
        }
        if (!triggered)
        {
            try
            {
                var gp = Gamepad.current;
                if (gp != null && gp.selectButton.wasPressedThisFrame)
                {
                    triggered = true;
                    allowSelectedFallback = true;
                }
            }
            catch { }
        }
        if (!triggered) return;
        if (!_firstRightClickLogged)
        {
            _firstRightClickLogged = true;
            Plugin.Log.LogInfo("[CardLock] premier toggle (clic droit, numpad . ou manette Select/Back) détecté");
        }

        // 1. Cartes en main (combat) : InteractableCard.IsHovering
        try
        {
            var ics = UObject.FindObjectsOfType<InteractableCard>();
            if (ics != null)
            {
                foreach (var ic in ics)
                {
                    try
                    {
                        if (ic == null || !ic.IsHovering) continue;
                        var cv = ic._cardView;
                        var cm = cv?._cardModel;
                        if (cm != null) { ToggleLock(cm); return; }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2. Cartes en modal (DeckBoxModal, OfferingTable...) : hover géométrique
        // + fallback EventSystem pour clavier/manette.
        try
        {
            var cscvs = UObject.FindObjectsOfType<CardSelectionCardView>();
            if (cscvs != null)
            {
                foreach (var cscv in cscvs)
                {
                    try
                    {
                        if (cscv == null || !hasPointerPos || !IsPointerOverCscv(cscv, pointerPos)) continue;
                        // Modal scenario : on passe par ToggleLockFromCSCV qui
                        // remappe le clic vers la vraie carte du joueur (via
                        // _allCards), pas le CardModel preview du modal qui a
                        // un GUID partagé entre toutes les cscv.
                        ToggleLockFromCSCV(cscv);
                        return;
                    }
                    catch { }
                }
            }
        }
        catch { }

        if (!allowSelectedFallback) return;
        try
        {
            var selectedCscv = GetSelectedCscv();
            if (selectedCscv != null) ToggleLockFromCSCV(selectedCscv);
        }
        catch { }
    }

    // Sprite cadenas doré dessiné en pixel-art (32x32) : disque sombre + cadenas
    // doré à l'intérieur. Identifiable même petit, sans dépendre d'une font emoji.
    static Sprite GetLockBadgeSprite()
    {
        if (_lockBadgeSprite != null) return _lockBadgeSprite;

        const int sz = 64;
        var tex = new Texture2D(sz, sz, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;
        var pixels = new Color[sz * sz];

        Color gold = new Color(1.00f, 0.82f, 0.18f, 1f);
        Color goldHi = new Color(1.00f, 0.95f, 0.55f, 1f);
        Color goldLo = new Color(0.62f, 0.42f, 0.05f, 1f);
        Color outline = new Color(0.10f, 0.07f, 0.02f, 1f);
        Color discBg = new Color(0.08f, 0.06f, 0.10f, 0.92f);
        Color discRim = new Color(1.00f, 0.78f, 0.20f, 1f);

        float cx = sz / 2f, cy = sz / 2f;
        // Géométrie cadenas (origine = centre image)
        const float bodyHalfW = 12f;     // demi-largeur du corps
        const float bodyTop = 4f;        // haut du corps (y > 0 = haut de l'image)
        const float bodyBot = -16f;      // bas du corps
        const float shackleR = 11f;      // rayon extérieur de l'arceau
        const float shackleThick = 3.2f; // épaisseur de l'arceau
        const float shackleCY = 4f;      // centre vertical de l'arc
        const float keyholeCY = -5f;     // centre du trou de serrure

        for (int y = 0; y < sz; y++)
        for (int x = 0; x < sz; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            Color col = Color.clear;

            if (dist <= 30f)
            {
                if (dist >= 27.5f) col = discRim;
                else if (dist >= 25f) col = outline;
                else col = discBg;
            }

            // Corps du cadenas (rectangle bas)
            bool inBody = (Mathf.Abs(dx) <= bodyHalfW && dy <= bodyTop && dy >= bodyBot);
            // Arceau (anneau supérieur)
            float ax = dx, ay = dy - shackleCY;
            float adist = Mathf.Sqrt(ax * ax + ay * ay);
            bool inShackleArc = (ay >= 0f && adist <= shackleR && adist >= shackleR - shackleThick);
            // Pieds de l'arceau qui descendent dans le corps
            bool inShackleLegs = (Mathf.Abs(Mathf.Abs(dx) - (shackleR - shackleThick / 2f)) <= shackleThick / 2f
                                  && dy >= -1f && dy < shackleCY);

            bool inLock = inBody || inShackleArc || inShackleLegs;
            if (inLock && dist <= 28f)
            {
                // Dégradé vertical sur le cadenas : reflets en haut, ombres en bas
                float br = Mathf.InverseLerp(bodyBot, shackleCY + shackleR, dy);
                Color mid = Color.Lerp(goldLo, gold, Mathf.Clamp01(br * 1.4f));
                Color top = Color.Lerp(mid, goldHi, Mathf.Clamp01((br - 0.55f) * 2.5f));
                col = top;
            }

            // Trou de serrure (rond + queue)
            if (inBody && dist <= 28f)
            {
                float kx = dx, ky = dy - keyholeCY;
                float kdist = Mathf.Sqrt(kx * kx + ky * ky);
                if (kdist <= 2.6f) col = outline;
                else if (Mathf.Abs(kx) <= 1.3f && ky <= 0f && ky >= -5f) col = outline;
            }

            pixels[y * sz + x] = col;
        }

        tex.SetPixels(pixels);
        tex.Apply();
        tex.hideFlags = HideFlags.HideAndDontSave;
        _lockBadgeSprite = Sprite.Create(tex, new Rect(0, 0, sz, sz), new Vector2(0.5f, 0.5f), 100f);
        _lockBadgeSprite.hideFlags = HideFlags.HideAndDontSave;
        return _lockBadgeSprite;
    }

    // Crée ou met à jour le badge cadenas sur un CardView. Idempotent : appelable
    // chaque frame sans recréer. Le badge est un enfant RectTransform ancré en
    // haut-droite de la carte. Si le CardView n'a pas de RectTransform parent
    // (carte 3D worldspace), la position visuelle peut être incorrecte mais
    // l'objet existe et reste cohérent — on traitera ce cas au test in-game.
    // Stratégie de positionnement : le _costText d'un CardView est toujours
    // positionné en haut-gauche de la carte. On l'utilise comme ancre fiable :
    // notre badge devient sibling du _costText et hérite du même système de
    // coords (UI canvas, worldspace canvas, ou sprite worldspace — peu importe).
    // On miroir la position en X pour avoir le coin haut-droite.
    private static bool _firstBadgeLogged = false;
    void EnsureLockBadge(CardView cardView, bool locked)
    {
        if (cardView == null) return;

        // Cache le texte du coût quand verrouillée. Cas spécial WILD : le
        // sprite contient déjà un W graphique → on garde le TMP text caché
        // en permanence. Détection via CardConfig.name (stable, pas affectée
        // par les modifs temporaires de combo qui changent le texte).
        try
        {
            var ct = cardView._costText;
            if (ct != null && ct.gameObject != null)
            {
                bool isWild = IsWildCard(cardView._cardModel);
                bool shouldBeActive = !locked && !isWild;
                if (ct.gameObject.activeSelf != shouldBeActive)
                    ct.gameObject.SetActive(shouldBeActive);
            }
        }
        catch { }

        // Anchor préféré : le PARENT de l'orbe → le badge est sibling de l'orbe
        // et hérite donc de l'animation/déplacement de la carte. Fallback sur
        // le _costText si l'orbe n'est pas trouvé.
        Transform orbT = null;
        try { orbT = FindNamedChild(cardView.gameObject.transform, "_manaComboElementOrb", 15); } catch { }
        Transform anchor = orbT?.parent;
        Transform costT = null;
        if (anchor == null)
        {
            TMP_Text costText = null;
            try { costText = cardView._costText; } catch { }
            if (costText == null || costText.transform == null) return;
            costT = costText.transform;
            anchor = costT.parent;
        }
        if (anchor == null) return;

        var existing = anchor.Find(LockBadgeName);
        if (existing != null)
        {
            bool wasActive = existing.gameObject.activeSelf;
            existing.gameObject.SetActive(locked);
            if (locked && !wasActive)
            {
                var cg = existing.GetComponent<CanvasGroup>();
                StartLockAnim(existing, cg);
            }
            return;
        }
        if (!locked) return;

        if (!_firstBadgeLogged)
        {
            _firstBadgeLogged = true;
            Plugin.Log.LogInfo($"[CardLock] premier badge : anchor={anchor.name} (orb={orbT != null})");
        }

        if (orbT != null) CreateLockBadgeOnOrb(orbT);
        else CreateLockBadgeUI(anchor, costT);
    }

    // Badge en SIBLING de l'orbe : auto-suit le mouvement/animation de la
    // carte (parenting hiérarchique). RectTransform values copiées exactement
    // de l'orbe → même taille et position pixel-perfect.
    void CreateLockBadgeOnOrb(Transform orbT)
    {
        var orbRT = orbT.GetComponent<RectTransform>();
        if (orbRT == null || orbT.parent == null) return;
        var parentT = orbT.parent;

        var badgeGo = new GameObject(LockBadgeName);
        badgeGo.layer = orbT.gameObject.layer;
        badgeGo.transform.SetParent(parentT, false);

        var rt = badgeGo.AddComponent<RectTransform>();
        rt.anchorMin = orbRT.anchorMin;
        rt.anchorMax = orbRT.anchorMax;
        rt.pivot = orbRT.pivot;
        rt.anchoredPosition = orbRT.anchoredPosition;
        rt.sizeDelta = orbRT.sizeDelta;
        rt.localScale = orbRT.localScale;
        badgeGo.transform.SetAsLastSibling();

        var cg = badgeGo.AddComponent<CanvasGroup>();
        cg.alpha = 0f;
        cg.interactable = false;
        cg.blocksRaycasts = false;

        // Couche 1 : anneau doré (intérieur transparent)
        var ringGo = new GameObject("Ring");
        ringGo.layer = orbT.gameObject.layer;
        ringGo.transform.SetParent(badgeGo.transform, false);
        var ringRT = ringGo.AddComponent<RectTransform>();
        ringRT.anchorMin = Vector2.zero; ringRT.anchorMax = Vector2.one;
        ringRT.offsetMin = ringRT.offsetMax = Vector2.zero;
        var ringImg = ringGo.AddComponent<Image>();
        ringImg.sprite = GetGoldenDiscSprite();
        ringImg.preserveAspect = true;
        ringImg.raycastTarget = false;

        // Couche 2 : cadenas (un peu plus grand que l'orbe pour dépasser
        // légèrement le ring → plus d'impact visuel)
        var lockGo = new GameObject("Lock");
        lockGo.layer = orbT.gameObject.layer;
        lockGo.transform.SetParent(badgeGo.transform, false);
        var lockRT = lockGo.AddComponent<RectTransform>();
        lockRT.anchorMin = Vector2.zero; lockRT.anchorMax = Vector2.one;
        lockRT.offsetMin = lockRT.offsetMax = Vector2.zero;
        lockGo.transform.localScale = new Vector3(1.15f, 1.15f, 1f);
        var lockImg = lockGo.AddComponent<Image>();
        lockImg.sprite = GetLockBadgeSprite();
        lockImg.preserveAspect = true;
        lockImg.raycastTarget = false;

        StartLockAnim(badgeGo.transform, cg);
    }

    void CreateLockBadgeUI(Transform anchor, Transform costT)
    {
        var badgeGo = new GameObject(LockBadgeName);
        badgeGo.layer = anchor.gameObject.layer;
        badgeGo.transform.SetParent(anchor, false);

        var rt = badgeGo.AddComponent<RectTransform>();
        var costRT = costT.GetComponent<RectTransform>();
        float side;
        if (costRT != null)
        {
            // Position du cost orb. La taille du _costText est plus grande que
            // l'orbe rendu → on shrink à 0.65× pour matcher visuellement l'orbe.
            rt.anchorMin = costRT.anchorMin;
            rt.anchorMax = costRT.anchorMax;
            rt.pivot = costRT.pivot;
            rt.anchoredPosition = costRT.anchoredPosition;
            float costSide = Mathf.Min(costRT.sizeDelta.x, costRT.sizeDelta.y);
            if (costSide < 30f) costSide = 60f;
            side = costSide * 0.65f;
            rt.sizeDelta = new Vector2(side, side);
        }
        else
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(6f, -6f);
            rt.sizeDelta = new Vector2(40f, 40f);
            side = 40f;
        }
        badgeGo.transform.SetAsLastSibling();

        // CanvasGroup permet de gérer l'alpha de tout le badge en bloc (anim fade-in)
        var cg = badgeGo.AddComponent<CanvasGroup>();
        cg.alpha = 0f;
        cg.interactable = false;
        cg.blocksRaycasts = false;

        // Couche 1 (arrière) : anneau doré (intérieur transparent → l'orbe natif est invisible derrière)
        var bgGo = new GameObject("Ring");
        bgGo.layer = anchor.gameObject.layer;
        bgGo.transform.SetParent(badgeGo.transform, false);
        var bgRT = bgGo.AddComponent<RectTransform>();
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = bgRT.offsetMax = Vector2.zero;
        var bgImg = bgGo.AddComponent<Image>();
        bgImg.sprite = GetGoldenDiscSprite();
        bgImg.preserveAspect = true;
        bgImg.raycastTarget = false;

        // Couche 2 (avant) : cadenas, presque pleine taille du rond
        var lockGo = new GameObject("Lock");
        lockGo.layer = anchor.gameObject.layer;
        lockGo.transform.SetParent(badgeGo.transform, false);
        var lockRT = lockGo.AddComponent<RectTransform>();
        lockRT.anchorMin = lockRT.anchorMax = new Vector2(0.5f, 0.5f);
        lockRT.pivot = new Vector2(0.5f, 0.5f);
        lockRT.anchoredPosition = Vector2.zero;
        lockRT.sizeDelta = new Vector2(side, side);
        var lockImg = lockGo.AddComponent<Image>();
        lockImg.sprite = GetLockBadgeSprite();
        lockImg.preserveAspect = true;
        lockImg.raycastTarget = false;

        // Anim fade-in zoom (badge démarre à 1.6× scale et alpha 0, finit à 1× / alpha 1)
        StartLockAnim(badgeGo.transform, cg);
    }

    // Anim de pop : le cadenas semble "se poser" sur la carte
    internal class LockPopAnim
    {
        public Transform badge;
        public CanvasGroup cg;
        public float startTime;
    }
    internal static readonly List<LockPopAnim> _lockPopAnims = new List<LockPopAnim>();
    const float LOCK_POP_DURATION = 0.25f;
    const float LOCK_POP_START_SCALE = 1.6f;

    void StartLockAnim(Transform badgeTransform, CanvasGroup cg)
    {
        if (badgeTransform == null) return;
        badgeTransform.localScale = new Vector3(LOCK_POP_START_SCALE, LOCK_POP_START_SCALE, 1f);
        if (cg != null) cg.alpha = 0f;
        _lockPopAnims.Add(new LockPopAnim
        {
            badge = badgeTransform,
            cg = cg,
            startTime = Time.unscaledTime
        });
    }

    void TickLockPopAnims()
    {
        if (_lockPopAnims.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = _lockPopAnims.Count - 1; i >= 0; i--)
        {
            var a = _lockPopAnims[i];
            if (a == null || a.badge == null) { _lockPopAnims.RemoveAt(i); continue; }
            float t = (now - a.startTime) / LOCK_POP_DURATION;
            if (t >= 1f)
            {
                a.badge.localScale = Vector3.one;
                if (a.cg != null) a.cg.alpha = 1f;
                _lockPopAnims.RemoveAt(i);
                continue;
            }
            // Ease-out cubic : démarre rapide, ralentit
            float ease = 1f - Mathf.Pow(1f - t, 3f);
            float scale = Mathf.Lerp(LOCK_POP_START_SCALE, 1f, ease);
            a.badge.localScale = new Vector3(scale, scale, 1f);
            if (a.cg != null) a.cg.alpha = ease;
        }
    }

    void CreateLockBadgeWorldspace(Transform anchor, Transform costT)
    {
        var badgeGo = new GameObject(LockBadgeName);
        badgeGo.layer = anchor.gameObject.layer;
        badgeGo.transform.SetParent(anchor, false);

        // Même position que le cost text (haut-gauche) : recouvre l'orbe de mana
        var p = costT.localPosition;
        badgeGo.transform.localPosition = new Vector3(p.x, p.y, p.z - 0.01f);
        // ~2× la taille du cost text pour recouvrir le rond
        var s = costT.localScale;
        badgeGo.transform.localScale = new Vector3(s.x * 2f, s.y * 2f, 1f);
        badgeGo.transform.localRotation = costT.localRotation;

        var sr = badgeGo.AddComponent<SpriteRenderer>();
        sr.sprite = GetLockBadgeSprite();
        sr.sortingOrder = 9999;
    }

    private static bool _firstScanLogged = false;
    private static bool _firstRightClickLogged = false;

    void ScanAndUpdateLockBadges()
    {
        if (--_lockBadgeScanCooldown > 0) return;
        _lockBadgeScanCooldown = 15;
        if (Plugin.CardLockEnabled == null || !Plugin.CardLockEnabled.Value) return;

        try
        {
            // Combat : itère directement la main du joueur (5 cartes max) au
            // lieu d'un FindObjectsOfType<CardView> qui scanne toute la scène.
            if (_cachedPlayerModel == null) _cachedPlayerModel = UObject.FindObjectOfType<PlayerModel>();
            var pm = _cachedPlayerModel;
            var hand = pm?.HandPile?.CardPile?._cards;
            if (hand != null)
            {
                int count = hand.Count;
                if (!_firstScanLogged && count > 0)
                {
                    _firstScanLogged = true;
                    Plugin.Log.LogInfo($"[CardLock] premier scan : {count} cartes en main");
                }
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var cm = hand[i];
                        var cv = cm?.CardView;
                        if (cv == null) continue;
                        EnsureLockBadge(cv, IsLocked(cm));
                    }
                    catch { }
                }
            }

            // Modal pile/défausse : seulement quand le DeckBoxModal est ouvert.
            // FindObjectsOfType<CardSelectionCardView> chaque scan = expensive
            // si on le fait toujours (scan complet de la scène).
            bool deckModalOpen = false;
            try
            {
                var deckModal = UObject.FindObjectOfType<DeckBoxModal>();
                deckModalOpen = deckModal != null && deckModal.IsOpen;
            }
            catch { }
            if (!deckModalOpen) return;

            var allCSCV = UObject.FindObjectsOfType<CardSelectionCardView>();
            if (allCSCV == null || allCSCV.Length == 0) return;
            foreach (var cscv in allCSCV)
            {
                try
                {
                    if (cscv == null) continue;
                    // Per-instance via rank mapping (cscv N de type X →
                    // Nème carte réelle de type X dans _allCards trié par GUID)
                    EnsureLockBadgeOnCSCV(cscv, IsCscvRealCardLocked(cscv, pm));
                }
                catch { }
            }
        }
        catch (Exception ex) { Plugin.Log.LogWarning($"[CardLock] scan error: {ex.Message}"); }
    }

    // Pour les modals : trouve l'orbe DANS le sous-arbre du CardSelectionCardView
    // (chaque cscv a sa propre instance d'orbe → pas de collision avec le pool).
    void EnsureLockBadgeOnCSCV(CardSelectionCardView cscv, bool locked)
    {
        if (cscv == null) return;
        Transform orbT = null;
        try { orbT = FindNamedChild(cscv.gameObject.transform, "_manaComboElementOrb", 15); } catch { }
        if (orbT == null) return;
        var anchor = orbT.parent;
        if (anchor == null) return;

        // Cache le cost text. Cas spécial WILD : détection via CardConfig.name
        // (stable, pas affectée par les modifs de combo).
        try
        {
            var ctT = FindNamedChild(cscv.gameObject.transform, "_manaCostText", 15);
            if (ctT != null)
            {
                CardModel cm = null;
                try { cm = cscv.CardModel?.TryCast<CardModel>(); } catch { }
                bool isWild = IsWildCard(cm);
                bool shouldBeActive = !locked && !isWild;
                if (ctT.gameObject.activeSelf != shouldBeActive)
                    ctT.gameObject.SetActive(shouldBeActive);
            }
        }
        catch { }

        var existing = anchor.Find(LockBadgeName);
        if (existing != null)
        {
            bool wasActive = existing.gameObject.activeSelf;
            existing.gameObject.SetActive(locked);
            if (locked && !wasActive)
            {
                var cg = existing.GetComponent<CanvasGroup>();
                StartLockAnim(existing, cg);
            }
            return;
        }
        if (!locked) return;

        CreateLockBadgeOnOrb(orbT);
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

