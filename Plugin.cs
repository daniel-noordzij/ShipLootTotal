using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShipLootTotal
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "DaanSmoki.LethalCompany.ShipLootTotal";
        public const string PluginName = "Ship Loot Total";
        public const string PluginVersion = "1.0.5";

        internal static ManualLogSource Log;
        internal static Harmony Harmony;
        internal static float _lastScanPostfixAt = -999f; // debounce
        internal static bool SuppressNextHudSfx = false;     // set by HUDHelper for our next popup only
        internal static bool SuppressHudSfxActive = false;   // set by HUD method prefix during that specific call

        internal static ConfigEntry<float> PopupDuration;

        private void Awake()
        {
            Log = Logger;
            Harmony = new Harmony(PluginGuid);

            PopupDuration = Config.Bind(
                "General",
                "PopupDuration",
                3f,
                "How long (in seconds) the popup stays visible after scanning."
            );

            Harmony.PatchAll();
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void OnDestroy()
        {
            Harmony?.UnpatchSelf();
        }
    }

    // Patch: scan input handler in your build lives on HUDManager
    [HarmonyPatch(typeof(HUDManager))]
    public static class Patch_HUD_PingScan
    {
        [HarmonyPostfix]
        [HarmonyPatch("PingScan_performed")]
        public static void Postfix(InputAction.CallbackContext context)
        {
            try
            {
                if (!context.performed) return;

                // Debounce multiple performed events fired rapidly
                if (Time.time - Plugin._lastScanPostfixAt < 0.25f) return;
                Plugin._lastScanPostfixAt = Time.time;

                var player = Utils.GetLocalPlayerController();
                if (player == null) return;
                if (!Utils.IsPlayerInShip(player)) return;

                int total = Utils.SumShipScrapValues();
                HUDHelper.ShowStable($"Total in Ship: {total}");

                Plugin.Log?.LogInfo($"ShipLootTotal: PingScan -> total={total}");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError(e);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_HUD_Display_SilentGate
    {
        // Patch DisplayGlobalNotification(string)
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var hudType = AccessTools.TypeByName("HUDManager");
            if (hudType == null) yield break;

            var global = AccessTools.Method(hudType, "DisplayGlobalNotification", new[] { typeof(string) });
            if (global != null) yield return global;
        }


        // Before HUD shows a message, arm/clear the active flag from the "next" flag
        static void Prefix()
        {
            Plugin.SuppressHudSfxActive = Plugin.SuppressNextHudSfx;
            Plugin.SuppressNextHudSfx = false; // consume it
        }

        // After the HUD call finishes, always drop the active flag
        static void Postfix()
        {
            Plugin.SuppressHudSfxActive = false;
        }
    }

    internal static class HUDHelper
    {
        // Cache HUD singleton + method (validated each call in case scene reloads)
        private static Type _hudType;
        private static object _hudInstance;
        private static MethodInfo _miDisplayGlobal;

        public static void ShowStable(string bodyText)
        {
            try
            {
                var hud = GetHUDManager();
                if (hud == null)
                {
                    Plugin.Log?.LogWarning("HUDHelper: HUDManager.Instance not found.");
                    return;
                }

                // Make this popup silent
                Plugin.SuppressNextHudSfx = true;

                // Ensure the panel is visible/active (alpha=1) before showing
                HudHideHelper.PrepareForShow(hud);

                // Cached method lookup
                if (_miDisplayGlobal == null || _miDisplayGlobal.DeclaringType == null)
                {
                    _miDisplayGlobal = hud.GetType().GetMethod(
                        "DisplayGlobalNotification",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new Type[] { typeof(string) },
                        null);
                }

                if (_miDisplayGlobal != null)
                {
                    _miDisplayGlobal.Invoke(hud, new object[] { bodyText });
                    HudHideHelper.HideAfterSeconds(Plugin.PopupDuration?.Value ?? 3f);
                    Plugin.Log?.LogInfo("HUDHelper: Used DisplayGlobalNotification(string) [silent].");
                }
                else
                {
                    Plugin.Log?.LogWarning("HUDHelper: DisplayGlobalNotification(string) not found.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"HUDHelper.ShowStable failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static object GetHUDManager()
        {
            // Type cache
            if (_hudType == null)
            {
                _hudType = AccessTools.TypeByName("HUDManager");
                if (_hudType == null) return null;
            }

            // Instance cache (Unity null-safe check) — and detect swaps
            var prop = _hudType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? _hudType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var current = prop?.GetValue(null);

            // Unity null checks for both current and cached
            bool cachedDead = _hudInstance is UnityEngine.Object cu && cu == null;
            bool currentDead = current is UnityEngine.Object nu && nu == null;

            // If first time, dead, or instance changed → update & reset audio locator
            if (_hudInstance == null || cachedDead || (!currentDead && !ReferenceEquals(_hudInstance, current)))
            {
                _hudInstance = current;
                HudAudioLocator.Reset();
            }

            return _hudInstance;
        }
    }

    internal static class HudHideHelper
    {
        private static Coroutine _hideRoutine;

        // Cache the panel + text (Unity-null aware)
        private static GameObject _panelGO;
        private static TMPro.TextMeshProUGUI _tmp;

        public static void PrepareForShow(object hud)
        {
            try
            {
                EnsureCachedParts(hud);

                if (_panelGO != null)
                {
                    if (!_panelGO.activeSelf) _panelGO.SetActive(true);
                    var cg = _panelGO.GetComponent<CanvasGroup>() ?? _panelGO.AddComponent<CanvasGroup>();
                    cg.alpha = 1f;
                    cg.interactable = false;
                    cg.blocksRaycasts = false;
                }

                if (_tmp != null)
                {
                    var textAlphaProp = _tmp.GetType().GetProperty("alpha");
                    if (textAlphaProp != null) textAlphaProp.SetValue(_tmp, 1f);
                }
            }
            catch { }
        }

        public static void HideAfterSeconds(float seconds)
        {
            var hud = HUDHelper.GetHUDManager();
            if (hud == null) return;

            var mono = hud as MonoBehaviour;
            if (mono == null) return;

            if (_hideRoutine != null)
                mono.StopCoroutine(_hideRoutine);

            _hideRoutine = mono.StartCoroutine(HideCoroutine(hud, seconds));
        }

        private static System.Collections.IEnumerator HideCoroutine(object hud, float seconds)
        {
            yield return new WaitForSeconds(seconds);

            try
            {
                var hudType = hud.GetType();

                // Stop default coroutine if tracked (prevents flicker)
                var fCo = hudType.GetField("globalNotificationCoroutine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fCo?.GetValue(hud) is Coroutine co && hud is MonoBehaviour mb)
                {
                    mb.StopCoroutine(co);
                    fCo.SetValue(hud, null);
                }

                EnsureCachedParts(hud);

                if (_tmp != null)
                    _tmp.text = string.Empty;

                if (_panelGO != null)
                {
                    var cg = _panelGO.GetComponent<CanvasGroup>() ?? _panelGO.AddComponent<CanvasGroup>();
                    cg.alpha = 0f;          // visually hidden (keep active for next time)
                    cg.interactable = false;
                    cg.blocksRaycasts = false;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"HudHideHelper: hide failed: {e.Message}");
            }
        }

        // Cache finder (runs only when needed or when Unity object was destroyed)
        private static void EnsureCachedParts(object hud)
        {
            if (_panelGO != null && _tmp != null) return;
            if (_panelGO is UnityEngine.Object p && p == null) _panelGO = null;
            if (_tmp is UnityEngine.Object t && t == null) _tmp = null;

            if (_panelGO != null && _tmp != null) return;

            var hudType = hud.GetType();

            if (_tmp == null)
            {
                var fText = hudType.GetField("globalNotificationText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _tmp = fText?.GetValue(hud) as TMPro.TextMeshProUGUI;
            }

            if (_panelGO == null)
            {
                var fBG = hudType.GetField("globalNotificationBackground", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var bgVal = fBG?.GetValue(hud);

                if (bgVal is GameObject go) _panelGO = go;
                else if (bgVal is Component comp) _panelGO = comp.gameObject;

                if (_panelGO == null && _tmp != null && _tmp.transform != null)
                {
                    var parent = _tmp.transform.parent;
                    if (parent != null) _panelGO = parent.gameObject;
                }
            }
        }
    }

    internal static class HudAudioLocator
    {
        private static AudioSource _cached;
        private static Type _hudType;
        private static object _hudInstance;

        public static void Reset()
        {
            _cached = null;
            _hudType = null;
            _hudInstance = null;
        }

        public static AudioSource GetHudAudio()
        {
            // If cached AudioSource got destroyed by scene/save swap, drop it
            if (_cached is UnityEngine.Object ao && ao == null)
                _cached = null;

            if (_cached != null)
                return _cached;

            // Re-acquire HUD type + instance (C# 7.3 compatible syntax)
            if (_hudType == null)
                _hudType = AccessTools.TypeByName("HUDManager");

            if (_hudType == null)
                return null;

            var prop = _hudType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null)
                prop = _hudType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var inst = prop != null ? prop.GetValue(null) : null;

            // If instance changed or is Unity-dead, forget cached and try again
            bool oldDead = _hudInstance is UnityEngine.Object hu && hu == null;
            bool newDead = inst is UnityEngine.Object hn && hn == null;
            if (_hudInstance == null || oldDead || (!newDead && !ReferenceEquals(_hudInstance, inst)))
            {
                _hudInstance = inst;
                _cached = null; // force re-find
            }

            if (_hudInstance == null)
                return null;

            // Try common fields/properties for UIAudio
            var f = _hudType.GetField("UIAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null)
                f = _hudType.GetField("uiAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (f != null)
                _cached = f.GetValue(_hudInstance) as AudioSource;

            if (_cached == null)
            {
                var p = _hudType.GetProperty("UIAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null)
                    p = _hudType.GetProperty("uiAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (p != null)
                    _cached = p.GetValue(_hudInstance) as AudioSource;
            }

            // Last resort: search under HUD object
            if (_cached == null && _hudInstance is Component hudComp)
                _cached = hudComp.GetComponentInChildren<AudioSource>(true);

            return _cached;
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new Type[] { typeof(AudioClip) })]
    public static class Patch_AudioSource_PlayOneShot_1
    {
        static bool Prefix(AudioSource __instance)
        {
            if (!Plugin.SuppressHudSfxActive) return true;

            var hudAudio = HudAudioLocator.GetHudAudio();
            if (hudAudio != null && __instance == hudAudio)
            {
                // Skip playing this sound — silent HUD popup for our call only
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new Type[] { typeof(AudioClip), typeof(float) })]
    public static class Patch_AudioSource_PlayOneShot_2
    {
        static bool Prefix(AudioSource __instance)
        {
            if (!Plugin.SuppressHudSfxActive) return true;

            var hudAudio = HudAudioLocator.GetHudAudio();
            if (hudAudio != null && __instance == hudAudio)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(HUDManager), "Awake")]
    public static class Patch_HUD_Awake_ResetAudioLocator
    {
        static void Postfix()
        {
            HudAudioLocator.Reset();
        }
    }

    internal static class Utils
    {
        // ---- Cached types / fields ----
        static Type _grabType;
        static FieldInfo _fi_itemProps, _fi_scrapValue, _fi_isInShipRoom, _fi_isInElevator;
        static FieldInfo _fi_ip_isScrap, _fi_ip_scrapValue;
        static FieldInfo _fi_isHeld, _fi_isPocketed, _fi_playerHeldBy;

        static Type _sorType, _gnmType, _pcbType;
        static PropertyInfo _pi_SOR_Instance;
        static FieldInfo _fi_SOR_localPlayerController;
        static PropertyInfo _pi_SOR_localPlayerController;

        static PropertyInfo _pi_GNM_Instance;
        static FieldInfo _fi_GNM_localPlayerController;
        static PropertyInfo _pi_GNM_localPlayerController;

        static FieldInfo _fi_PCB_isInHangar, _fi_PCB_isInShip, _fi_PCB_isLocal, _fi_PCB_isOwner;

        // ---- Cached object list (refresh window) ----
        static UnityEngine.Object[] _grabbablesCache = Array.Empty<UnityEngine.Object>();
        static float _lastCacheAt = -999f;
        private const float CacheWindowSeconds = 2f;

        // ---- Ship bounds cache ----
        static Bounds _shipBounds;
        static float _shipBoundsLastBuild = -999f;
        private const float ShipBoundsRebuildSeconds = 3f;

        public static object GetLocalPlayerController()
        {
            if (_sorType == null)
            {
                _sorType = AccessTools.TypeByName("StartOfRound");
                if (_sorType != null)
                {
                    _pi_SOR_Instance = _sorType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_SOR_localPlayerController = _sorType.GetField("localPlayerController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _pi_SOR_localPlayerController = _sorType.GetProperty("localPlayerController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            if (_sorType != null && _pi_SOR_Instance != null)
            {
                var sor = _pi_SOR_Instance.GetValue(null);
                if (sor != null)
                {
                    var lpc = _fi_SOR_localPlayerController?.GetValue(sor)
                           ?? _pi_SOR_localPlayerController?.GetValue(sor);
                    if (lpc != null) return lpc;
                }
            }

            if (_gnmType == null)
            {
                _gnmType = AccessTools.TypeByName("GameNetworkManager");
                if (_gnmType != null)
                {
                    _pi_GNM_Instance = _gnmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_GNM_localPlayerController = _gnmType.GetField("localPlayerController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _pi_GNM_localPlayerController = _gnmType.GetProperty("localPlayerController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            if (_gnmType != null && _pi_GNM_Instance != null)
            {
                var gnm = _pi_GNM_Instance.GetValue(null);
                if (gnm != null)
                {
                    var lpc = _fi_GNM_localPlayerController?.GetValue(gnm)
                           ?? _pi_GNM_localPlayerController?.GetValue(gnm);
                    if (lpc != null) return lpc;
                }
            }

            return null;
        }

        public static bool IsPlayerInShip(object playerControllerB)
        {
            if (playerControllerB == null) return false;

            if (_pcbType == null)
            {
                _pcbType = playerControllerB.GetType();
                _fi_PCB_isInHangar = _pcbType.GetField("isInHangarShipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _fi_PCB_isInShip = _pcbType.GetField("isInShipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _fi_PCB_isLocal = _pcbType.GetField("isLocalPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _fi_PCB_isOwner = _pcbType.GetField("IsOwner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            // Only enforce these if the fields exist and have a value
            bool? isLocal = _fi_PCB_isLocal?.GetValue(playerControllerB) as bool?;
            if (isLocal.HasValue && !isLocal.Value) return false;

            bool? isOwner = _fi_PCB_isOwner?.GetValue(playerControllerB) as bool?;
            if (isOwner.HasValue && !isOwner.Value) return false;

            bool? inHangar = _fi_PCB_isInHangar?.GetValue(playerControllerB) as bool?;
            if (inHangar.HasValue && inHangar.Value) return true;

            bool? inShip = _fi_PCB_isInShip?.GetValue(playerControllerB) as bool?;
            if (inShip.HasValue) return inShip.Value;

            // Fallback: inside ship bounds
            var shipB = GetShipBounds();
            var tr = (playerControllerB as Component)?.transform
                  ?? _pcbType.GetProperty("transform")?.GetValue(playerControllerB) as Transform;
            if (tr != null && shipB.size != Vector3.zero)
                return shipB.Contains(tr.position);

            return false;
        }

        private static Transform GetShipRootTransform()
        {
            if (_sorType == null) return null;
            var inst = _pi_SOR_Instance?.GetValue(null);
            if (inst == null) return null;

            var shipRoom = _sorType.GetField("shipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(inst) as GameObject;
            if (shipRoom != null) return shipRoom.transform;

            var hangarShip = _sorType.GetField("hangarShip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(inst) as GameObject;
            if (hangarShip != null) return hangarShip.transform;

            var shipFloor = _sorType.GetField("shipFloor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(inst) as GameObject;
            if (shipFloor != null) return shipFloor.transform;

            return null;
        }

        private static Bounds GetShipBounds()
        {
            // Rebuild occasionally
            if (Time.time - _shipBoundsLastBuild <= ShipBoundsRebuildSeconds && _shipBounds.size != Vector3.zero)
                return _shipBounds;

            _shipBoundsLastBuild = Time.time;
            _shipBounds = new Bounds();

            var root = GetShipRootTransform();
            if (root == null) return _shipBounds;

            var colliders = root.GetComponentsInChildren<Collider>(true);
            if (colliders != null && colliders.Length > 0)
            {
                _shipBounds = colliders[0].bounds;
                for (int i = 1; i < colliders.Length; i++)
                    _shipBounds.Encapsulate(colliders[i].bounds);

                _shipBounds.Expand(0.5f); // forgiving threshold
            }

            return _shipBounds;
        }

        // ---------- Scrap total with caching, strict filtering, and precise location ----------
        private static UnityEngine.Object[] GetGrabbables()
        {
            if (_grabType == null)
            {
                _grabType = AccessTools.TypeByName("GrabbableObject");
                if (_grabType != null)
                {
                    _fi_itemProps = _grabType.GetField("itemProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_scrapValue = _grabType.GetField("scrapValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_isInShipRoom = _grabType.GetField("isInShipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_isInElevator = _grabType.GetField("isInElevator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_isHeld = _grabType.GetField("isHeld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_isPocketed = _grabType.GetField("isPocketed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _fi_playerHeldBy = _grabType.GetField("playerHeldBy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            if (_grabType == null) return Array.Empty<UnityEngine.Object>();

            if (Time.time - _lastCacheAt > CacheWindowSeconds || _grabbablesCache == null)
            {
                _lastCacheAt = Time.time;
                _grabbablesCache = UnityEngine.Object.FindObjectsOfType(_grabType) as UnityEngine.Object[] ?? Array.Empty<UnityEngine.Object>();
            }

            return _grabbablesCache;
        }

        public static int SumShipScrapValues()
        {
            var list = GetGrabbables();
            if (list == null || list.Length == 0) return 0;

            // Prepare ItemProperties reflection once (avoid LINQ alloc)
            if (_fi_ip_isScrap == null || _fi_ip_scrapValue == null)
            {
                UnityEngine.Object any = null;
                for (int i = 0; i < list.Length; i++)
                {
                    if (list[i] != null) { any = list[i]; break; }
                }

                if (any != null && _fi_itemProps != null)
                {
                    var ip = _fi_itemProps.GetValue(any);
                    if (ip != null)
                    {
                        var ipt = ip.GetType();
                        _fi_ip_isScrap = _fi_ip_isScrap ?? ipt.GetField("isScrap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        _fi_ip_scrapValue = _fi_ip_scrapValue ?? ipt.GetField("scrapValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
            }

            int total = 0;
            var shipB = GetShipBounds();
            var shipRoot = GetShipRootTransform();

            for (int i = 0; i < list.Length; i++)
            {
                var go = list[i];
                if (go == null) continue;

                // Get transform once (reuse below)
                var tr = (go as Component)?.transform;

                // ---- STRICT SCRAP FILTER ----
                bool isScrap = false;
                int val = 0;

                if (_fi_itemProps != null)
                {
                    var ip = _fi_itemProps.GetValue(go);
                    if (ip != null && _fi_ip_isScrap != null)
                    {
                        var os = _fi_ip_isScrap.GetValue(ip);
                        if (os is bool && (bool)os) isScrap = true;
                    }

                    if (!isScrap) continue; // ignore non-sellables

                    if (_fi_ip_scrapValue != null)
                    {
                        var ov = _fi_ip_scrapValue.GetValue(ip);
                        if (ov is int) val = Math.Max(val, (int)ov);
                    }
                }
                else
                {
                    // If itemProperties missing, be conservative: skip
                    continue;
                }

                if (_fi_scrapValue != null)
                {
                    var osv = _fi_scrapValue.GetValue(go);
                    if (osv is int) val = Math.Max(val, (int)osv);
                }

                if (val <= 0) continue;

                // ---- SKIP HELD/POCKETED ----
                if (_fi_isHeld != null)
                {
                    var oh = _fi_isHeld.GetValue(go);
                    if (oh is bool && (bool)oh) continue;
                }
                if (_fi_isPocketed != null)
                {
                    var op = _fi_isPocketed.GetValue(go);
                    if (op is bool && (bool)op) continue;
                }
                if (_fi_playerHeldBy != null && _fi_playerHeldBy.GetValue(go) != null) continue;

                // ---- IN SHIP CHECK ----
                bool inShip = false;

                // Direct flag
                if (_fi_isInShipRoom != null)
                {
                    var ir = _fi_isInShipRoom.GetValue(go);
                    if (ir is bool && (bool)ir) inShip = true;
                }

                // Treat in-elevator as in ship ONLY if near ship root (avoid map elevators)
                if (!inShip && _fi_isInElevator != null)
                {
                    var ie = _fi_isInElevator.GetValue(go);
                    if (ie is bool && (bool)ie)
                    {
                        if (shipRoot != null && tr != null)
                        {
                            float dist = Vector3.Distance(tr.position, shipRoot.position);
                            if (dist < 25f) inShip = true;
                        }
                    }
                }

                // Bounds fallback for extra safety (reuse tr)
                if (!inShip && shipB.size != Vector3.zero && tr != null)
                {
                    inShip = shipB.Contains(tr.position);
                }

                if (!inShip) continue;

                total += val;
            }

            return total;
        }
    }
}
