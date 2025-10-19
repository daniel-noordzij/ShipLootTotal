using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShipLootTotal
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "DaanSmoki.LethalCompany.ShipLootTotal";
        public const string PluginName = "Ship Loot Total";
        public const string PluginVersion = "1.1.2";

        internal static ManualLogSource Log;
        internal static Harmony Harmony;

        internal static float _lastScanPostfixAt = -999f;   // debounce for scan action
        internal static bool SuppressNextHudSfx = false;     // armed by HUDHelper for the next popup only
        internal static bool SuppressHudSfxActive = false;   // toggled by HUD prefix for that specific call

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
            ScrapValueSyncPatcher.Apply(Harmony);

            Log.LogInfo(PluginName + " " + PluginVersion + " loaded.");
        }
    }

    // Patch: Player scan input handler on HUDManager
    [HarmonyPatch(typeof(HUDManager))]
    public static class Patch_HUD_PingScan
    {
        [HarmonyPostfix]
        [HarmonyPatch("PingScan_performed")]
        public static void Postfix(InputAction.CallbackContext context)
        {
            try
            {
                if (!context.performed)
                    return;

                // Debounce rapid events
                if (Time.time - Plugin._lastScanPostfixAt < 0.25f)
                    return;
                Plugin._lastScanPostfixAt = Time.time;

                var player = Utils.GetLocalPlayerController();
                if (player == null)
                    return;
                if (!Utils.IsPlayerInShip(player))
                    return;

                Utils.InvalidateGrabbablesCache();
                int total = Utils.SumShipScrapValues();
                HUDHelper.ShowStable("Total in Ship: " + total);

                if (Plugin.Log != null) Plugin.Log.LogInfo("ShipLootTotal: PingScan -> total=" + total);
            }
            catch (Exception e)
            {
                if (Plugin.Log != null) Plugin.Log.LogError(e);
            }
        }
    }

    // Gate HUD SFX for our next popup only
    [HarmonyPatch]
    public static class Patch_HUD_Display_SilentGate
    {
        // Target DisplayGlobalNotification(string)
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var hudType = AccessTools.TypeByName("HUDManager");
            if (hudType == null) yield break;

            var global = AccessTools.Method(hudType, "DisplayGlobalNotification", new[] { typeof(string) });
            if (global != null) yield return global;
        }

        static void Prefix()
        {
            Plugin.SuppressHudSfxActive = Plugin.SuppressNextHudSfx;
            Plugin.SuppressNextHudSfx = false; // consume arm
        }

        static void Postfix()
        {
            Plugin.SuppressHudSfxActive = false;
        }
    }

    internal static class HUDHelper
    {
        // Cached references (validated each call in case scenes reload)
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
                    if (Plugin.Log != null) Plugin.Log.LogWarning("HUDHelper: HUDManager.Instance not found.");
                    return;
                }

                // Make this popup silent
                Plugin.SuppressNextHudSfx = true;

                // Ensure panel is visible before showing
                HudHideHelper.PrepareForShow(hud);

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
                    HudHideHelper.HideAfterSeconds(Plugin.PopupDuration != null ? Plugin.PopupDuration.Value : 3f);
                    if (Plugin.Log != null) Plugin.Log.LogInfo("HUDHelper: Used DisplayGlobalNotification(string) [silent].");
                }
                else if (Plugin.Log != null)
                {
                    Plugin.Log.LogWarning("HUDHelper: DisplayGlobalNotification(string) not found.");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Log != null) Plugin.Log.LogWarning("HUDHelper.ShowStable failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static object GetHUDManager()
        {
            if (_hudType == null)
            {
                _hudType = AccessTools.TypeByName("HUDManager");
                if (_hudType == null) return null;
            }

            var prop = _hudType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? _hudType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var current = prop != null ? prop.GetValue(null) : null;

            bool cachedDead = _hudInstance is UnityEngine.Object && (UnityEngine.Object)_hudInstance == null;
            bool currentDead = current is UnityEngine.Object && (UnityEngine.Object)current == null;

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

        private static IEnumerator HideCoroutine(object hud, float seconds)
        {
            yield return new WaitForSeconds(seconds);

            try
            {
                var hudType = hud.GetType();

                // Stop default coroutine if tracked (prevents flicker)
                var fCo = hudType.GetField("globalNotificationCoroutine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fCo != null)
                {
                    var val = fCo.GetValue(hud);
                    var co = val as Coroutine;
                    var mb = hud as MonoBehaviour;
                    if (co != null && mb != null)
                    {
                        mb.StopCoroutine(co);
                        fCo.SetValue(hud, null);
                    }
                }

                EnsureCachedParts(hud);

                if (_tmp != null)
                    _tmp.text = string.Empty;

                if (_panelGO != null)
                {
                    var cg = _panelGO.GetComponent<CanvasGroup>() ?? _panelGO.AddComponent<CanvasGroup>();
                    cg.alpha = 0f; // visually hidden (keep active for next time)
                    cg.interactable = false;
                    cg.blocksRaycasts = false;
                }
            }
            catch (Exception e)
            {
                if (Plugin.Log != null) Plugin.Log.LogWarning("HudHideHelper: hide failed: " + e.Message);
            }
        }

        private static void EnsureCachedParts(object hud)
        {
            if (_panelGO != null && _tmp != null) return;

            if (_panelGO is UnityEngine.Object && (UnityEngine.Object)_panelGO == null) _panelGO = null;
            if (_tmp is UnityEngine.Object && (UnityEngine.Object)_tmp == null) _tmp = null;

            if (_panelGO != null && _tmp != null) return;

            var hudType = hud.GetType();

            if (_tmp == null)
            {
                var fText = hudType.GetField("globalNotificationText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _tmp = fText != null ? fText.GetValue(hud) as TMPro.TextMeshProUGUI : null;
            }

            if (_panelGO == null)
            {
                var fBG = hudType.GetField("globalNotificationBackground", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var bgVal = fBG != null ? fBG.GetValue(hud) : null;

                var go = bgVal as GameObject;
                if (go != null) _panelGO = go;
                else
                {
                    var comp = bgVal as Component;
                    if (comp != null) _panelGO = comp.gameObject;
                }

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
            if (_cached is UnityEngine.Object && (UnityEngine.Object)_cached == null)
                _cached = null;

            if (_cached != null)
                return _cached;

            if (_hudType == null)
                _hudType = AccessTools.TypeByName("HUDManager");

            if (_hudType == null)
                return null;

            var prop = _hudType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                       ?? _hudType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var inst = prop != null ? prop.GetValue(null) : null;

            bool oldDead = _hudInstance is UnityEngine.Object && (UnityEngine.Object)_hudInstance == null;
            bool newDead = inst is UnityEngine.Object && (UnityEngine.Object)inst == null;

            if (_hudInstance == null || oldDead || (!newDead && !ReferenceEquals(_hudInstance, inst)))
            {
                _hudInstance = inst;
                _cached = null; // force re-find
            }

            if (_hudInstance == null)
                return null;

            var f = _hudType.GetField("UIAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? _hudType.GetField("uiAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
                _cached = f.GetValue(_hudInstance) as AudioSource;

            if (_cached == null)
            {
                var p = _hudType.GetProperty("UIAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? _hudType.GetProperty("uiAudio", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null)
                    _cached = p.GetValue(_hudInstance) as AudioSource;
            }

            if (_cached == null)
            {
                var hudComp = _hudInstance as Component;
                if (hudComp != null)
                    _cached = hudComp.GetComponentInChildren<AudioSource>(true);
            }

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
                return false;

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
        static UnityEngine.Object[] _grabbablesCache = new UnityEngine.Object[0];
        static float _lastCacheAt = -999f;
        private const float CacheWindowSeconds = 2f;

        // ---- Ship bounds cache ----
        static Bounds _shipBounds;
        static float _shipBoundsLastBuild = -999f;
        private const float ShipBoundsRebuildSeconds = 3f;
        private const float ShipBoundsPadding = 8f; // generous padding to avoid false negatives

        // ---- Parent-name heuristic (helps in scenes where flags/bounds lag) ----
        private static readonly string[] _shipNameNeedles = new string[] { "ship", "hangar" };
        private const int ParentHeuristicMaxDepth = 24;

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
                    var lpc = _fi_SOR_localPlayerController != null ? _fi_SOR_localPlayerController.GetValue(sor) : null;
                    if (lpc == null && _pi_SOR_localPlayerController != null)
                        lpc = _pi_SOR_localPlayerController.GetValue(sor);
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
                    var lpc2 = _fi_GNM_localPlayerController != null ? _fi_GNM_localPlayerController.GetValue(gnm) : null;
                    if (lpc2 == null && _pi_GNM_localPlayerController != null)
                        lpc2 = _pi_GNM_localPlayerController.GetValue(gnm);
                    if (lpc2 != null) return lpc2;
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

            bool? isLocal = _fi_PCB_isLocal != null ? (bool?)_fi_PCB_isLocal.GetValue(playerControllerB) : null;
            if (isLocal.HasValue && !isLocal.Value) return false;

            bool? isOwner = _fi_PCB_isOwner != null ? (bool?)_fi_PCB_isOwner.GetValue(playerControllerB) : null;
            if (isOwner.HasValue && !isOwner.Value) return false;

            bool? inHangar = _fi_PCB_isInHangar != null ? (bool?)_fi_PCB_isInHangar.GetValue(playerControllerB) : null;
            if (inHangar.HasValue && inHangar.Value) return true;

            bool? inShip = _fi_PCB_isInShip != null ? (bool?)_fi_PCB_isInShip.GetValue(playerControllerB) : null;
            if (inShip.HasValue) return inShip.Value;

            var shipB = GetShipBounds();
            Transform tr = null;
            var comp = playerControllerB as Component;
            if (comp != null) tr = comp.transform;
            else
            {
                var prop = _pcbType.GetProperty("transform");
                if (prop != null) tr = prop.GetValue(playerControllerB, null) as Transform;
            }

            if (tr != null && shipB.size != Vector3.zero)
                return shipB.Contains(tr.position);

            return false;
        }

        private static Transform GetShipRootTransform()
        {
            if (_sorType == null) return null;
            var inst = _pi_SOR_Instance != null ? _pi_SOR_Instance.GetValue(null) : null;
            if (inst == null) return null;

            var shipRoom = _sorType.GetField("shipRoom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (shipRoom != null)
            {
                var go = shipRoom.GetValue(inst) as GameObject;
                if (go != null) return go.transform;
            }

            var hangarShip = _sorType.GetField("hangarShip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (hangarShip != null)
            {
                var go2 = hangarShip.GetValue(inst) as GameObject;
                if (go2 != null) return go2.transform;
            }

            var shipFloor = _sorType.GetField("shipFloor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (shipFloor != null)
            {
                var go3 = shipFloor.GetValue(inst) as GameObject;
                if (go3 != null) return go3.transform;
            }

            return null;
        }

        private static Bounds GetShipBounds()
        {
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

                // Expand generously to catch edge placements / elevator lip
                _shipBounds.Expand(ShipBoundsPadding);
            }

            if (_shipBounds.size == Vector3.zero)
            {
                if (Plugin.Log != null) Plugin.Log.LogInfo("[ShipLootTotal] Ship bounds empty, scheduling delayed rebuild.");
                new GameObject("ShipBoundsRebuilder").AddComponent<ShipBoundsRebuilder>();
            }

            return _shipBounds;
        }

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

            if (_grabType == null) return new UnityEngine.Object[0];

            if (Time.time - _lastCacheAt > CacheWindowSeconds || _grabbablesCache == null)
            {
                _lastCacheAt = Time.time;
                var found = UnityEngine.Object.FindObjectsOfType(_grabType) as UnityEngine.Object[];
                if (found == null || found.Length == 0)
                {
                    var all = Resources.FindObjectsOfTypeAll(_grabType) as UnityEngine.Object[];
                    if (all != null && all.Length > 0)
                    {
                        var list = new System.Collections.Generic.List<UnityEngine.Object>(all.Length);
                        for (int i = 0; i < all.Length; i++)
                        {
                            var comp = all[i] as Component;
                            if (comp == null) continue;
                            var go = comp.gameObject;
                            if (!go.scene.IsValid()) continue; // skip assets/prefabs
                            list.Add(all[i]);
                        }
                        found = list.ToArray();
                    }
                }
                _grabbablesCache = found != null ? found : new UnityEngine.Object[0];
            }

            return _grabbablesCache;
        }

        public static int SumShipScrapValues()
        {
            var list = GetGrabbables();
            if (list == null || list.Length == 0) return 0;

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
                        if (_fi_ip_isScrap == null) _fi_ip_isScrap = ipt.GetField("isScrap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (_fi_ip_scrapValue == null) _fi_ip_scrapValue = ipt.GetField("scrapValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

                var tr = (go as Component) != null ? ((Component)go).transform : null;

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

                    if (!isScrap) continue;

                    if (_fi_ip_scrapValue != null)
                    {
                        var ov = _fi_ip_scrapValue.GetValue(ip);
                        if (ov is int) val = Math.Max(val, (int)ov);
                    }
                }
                else
                {
                    continue;
                }

                if (_fi_scrapValue != null)
                {
                    var osv = _fi_scrapValue.GetValue(go);
                    if (osv is int) val = Math.Max(val, (int)osv);
                }

                if (val <= 0) continue;

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

                bool inShip = false;

                // 1) Trust the game flags when present
                if (_fi_isInShipRoom != null)
                {
                    var ir = _fi_isInShipRoom.GetValue(go);
                    if (ir is bool && (bool)ir) inShip = true;
                }

                // 2) Elevator counts as in-ship without distance requirement
                if (!inShip && _fi_isInElevator != null)
                {
                    var ie = _fi_isInElevator.GetValue(go);
                    if (ie is bool && (bool)ie)
                        inShip = true;
                }

                // 3) Parent-name heuristic (fast and resilient)
                if (!inShip && tr != null)
                {
                    int hops = 0; var p = tr;
                    while (p != null && hops < ParentHeuristicMaxDepth)
                    {
                        var n = p.name;
                        if (!string.IsNullOrEmpty(n))
                        {
                            var ln = n.ToLowerInvariant();
                            for (int nn = 0; nn < _shipNameNeedles.Length; nn++)
                            {
                                if (ln.IndexOf(_shipNameNeedles[nn], StringComparison.Ordinal) >= 0)
                                {
                                    inShip = true;
                                    break;
                                }
                            }
                            if (inShip) break;
                        }
                        p = p.parent; hops++;
                    }
                }

                // 4) Bounds check (with generous padding baked into bounds)
                if (!inShip && tr != null && shipB.size != Vector3.zero)
                {
                    if (shipB.Contains(tr.position)) inShip = true;
                }

                // 5) Proximity to ship root (horizontal distance)
                if (!inShip && shipRoot != null && tr != null)
                {
                    var a = tr.position; var b = shipRoot.position; a.y = b.y;
                    if (Vector3.Distance(a, b) < 30f) inShip = true;
                }

                if (!inShip) continue;

                total += val;
            }

            if (Plugin.Log != null) Plugin.Log.LogInfo("[DEBUG] Total=" + total + ", Objects=" + list.Length + ", Bounds=" + GetShipBounds().size);
            return total;
        }

        public static void InvalidateGrabbablesCache()
        {
            _grabbablesCache = new UnityEngine.Object[0];
            _lastCacheAt = -999f;
        }
    }

    internal static class ScrapValueSyncPatcher
    {
        public static void Apply(Harmony harmony)
        {
            var t = AccessTools.TypeByName("GrabbableObject");
            if (t == null)
            {
                if (Plugin.Log != null) Plugin.Log.LogWarning("ScrapValueSyncPatcher: GrabbableObject type not found.");
                return;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var methods = t.GetMethods(flags);

            string[] nameNeedles = new string[] { "ClientRpc", "SetScrap", "SyncScrap", "UpdateScrap", "ScrapValue" };

            int patched = 0;

            for (int i = 0; i < methods.Length; i++)
            {
                var m = methods[i];
                var lname = m.Name.ToLowerInvariant();

                if (lname.IndexOf("scrap", StringComparison.Ordinal) < 0) continue;

                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(int)) continue;

                bool looksGood = false;
                for (int k = 0; k < nameNeedles.Length; k++)
                {
                    if (lname.IndexOf(nameNeedles[k].ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    {
                        looksGood = true;
                        break;
                    }
                }
                if (!looksGood) continue;

                try
                {
                    var postfix = new HarmonyMethod(
                        typeof(ScrapValueSyncPatcher).GetMethod("ScrapValuePostfix", BindingFlags.Static | BindingFlags.NonPublic));

                    harmony.Patch(m, null, postfix);
                    patched++;
                }
                catch (Exception e)
                {
                    if (Plugin.Log != null) Plugin.Log.LogWarning("ScrapValueSyncPatcher: failed to patch " + m.Name + " : " + e.Message);
                }
            }

            if (Plugin.Log != null) Plugin.Log.LogInfo("ScrapValueSyncPatcher: patched " + patched + " scrap sync method(s).");
        }

        private static void ScrapValuePostfix(object __instance, object[] __args)
        {
            try
            {
                if (__instance == null || __args == null || __args.Length == 0) return;
                var a0 = __args[0];
                if (!(a0 is int)) return;
                int value = (int)a0;

                var fi = __instance.GetType().GetField("scrapValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null)
                {
                    fi.SetValue(__instance, value);
                    Utils.InvalidateGrabbablesCache();
                }
            }
            catch { }
        }
    }

    public class ShipBoundsRebuilder : MonoBehaviour
    {
        private IEnumerator Start()
        {
            // Wait a bit for colliders to finish spawning on client
            yield return new WaitForSeconds(5f);
            Utils.InvalidateGrabbablesCache();

            var bounds = typeof(Utils)
                .GetMethod("GetShipBounds", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);

            if (Plugin.Log != null) Plugin.Log.LogInfo("[ShipLootTotal] Delayed ship bounds rebuilt: " + bounds);
            Destroy(gameObject);
        }
    }

    [HarmonyPatch(typeof(StartOfRound), "SyncAlreadyHeldObjectsClientRpc")]
    public static class Patch_SOR_AfterHeldSync
    {
        static void Postfix()
        {
            Plugin._lastScanPostfixAt = -999f;
        }
    }
}
