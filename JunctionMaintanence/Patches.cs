// File: Patches.cs
// Namespace: JunctionMaintanence
// Contains: Save/Load Harmony patches, gameplay patches (forced damage, random flip), SpeedEstimator
//           + Block manual switching at 100% damage (toggleable via Settings)
//           + TrainCarQuery (snapshot-based, no FindObjectsOfType)

using System;
using HarmonyLib;
using UnityEngine;
using DV;
using Newtonsoft.Json.Linq;

namespace JunctionMaintanence
{
    // LOAD hook: when save becomes current, read our map
    [HarmonyPatch(typeof(StartGameData_FromSaveGame), "MakeCurrent")]
    static class JM_Patch_LoadFromSave
    {
        static void Postfix(StartGameData_FromSaveGame __instance)
        {
            try
            {
                var saveData = Traverse.Create(__instance).Field("saveGameData").GetValue<SaveGameData>();
                if (saveData == null) return;

                var dataObject = Traverse.Create(saveData).Field("dataObject").GetValue<JObject>();
                if (dataObject == null)
                {
                    Main.Log("SaveGameData.dataObject is null – aborting load.", true);
                    return;
                }

                if (dataObject.TryGetValue(JM_Save.KEY, out JToken token) && token != null && token.Type != JTokenType.Null)
                {
                    var map = JM_Save.FromToken(token);
                    DamageStore.ReplaceAll(map);
                    Main.Log($"Loaded {map.Count} junction damage entries from save.");
                }
                else
                {
                    Main.Log("No JunctionMaintanence key in savegame – nothing to load.");
                }
            }
            catch (Exception e)
            {
                Main.Log("LoadFromSave exception: " + e, true);
            }
        }
    }

    // SAVE hook: before saving, write our map
    [HarmonyPatch(typeof(SaveGameManager), "Save")]
    static class JM_Patch_SaveGame
    {
        static void Prefix()
        {
            try
            {
                var saveData = SaveGameManager.Instance?.data;
                if (saveData == null) return;

                var trav = Traverse.Create(saveData).Field("dataObject");
                var dataObject = trav.GetValue<JObject>();
                if (dataObject == null)
                {
                    dataObject = new JObject();
                    trav.SetValue(dataObject);
                }

                var arr = JM_Save.ToJsonArray(Main.Settings.DamageMap);
                dataObject[JM_Save.KEY] = arr;
                Main.Log($"Saved {Main.Settings.DamageMap.Count} damage entries into savegame.");
            }
            catch (Exception e)
            {
                Main.Log("SaveGame Prefix exception: " + e, true);
            }
        }
    }

    // New career init: ensure key exists
    [HarmonyPatch(typeof(StartGameData_NewCareer), "PrepareNewSaveData")]
    static class JM_Patch_NewCareerInit
    {
        static void Postfix(ref SaveGameData saveGameData)
        {
            try
            {
                if (saveGameData == null) return;
                var trav = Traverse.Create(saveGameData).Field("dataObject");
                var dataObject = trav.GetValue<JObject>();
                if (dataObject == null)
                {
                    dataObject = new JObject();
                    trav.SetValue(dataObject);
                }

                if (!dataObject.ContainsKey(JM_Save.KEY))
                {
                    dataObject[JM_Save.KEY] = JM_Save.ToJsonArray(Main.Settings.DamageMap);
                    Main.Log("New career: initialized JunctionMaintanence map.");
                }
            }
            catch (Exception e)
            {
                Main.Log("NewCareerInit exception: " + e, true);
            }
        }
    }

    // ------------------- BLOCK MANUAL SWITCHING AT 100% DAMAGE -------------------

    internal static class _JM_SwitchBlockHelper
    {
        public static bool AllowSwitch(Junction j, Junction.SwitchMode mode)
        {
            if (!Main.Enabled) return true;

            // Setting: Feature global abschaltbar
            if (!Main.Settings.BlockManualSwitchAtFullDamage) return true;

            // Forced run-throughs sollen weiter funktionieren (Schadensmodell/Postfix).
            if (mode == Junction.SwitchMode.FORCED) return true;

            try
            {
                string key = DamageStore.MakeKey(j);
                float dmg = DamageStore.Get(key); // 0..1
                if (dmg >= 0.999f)
                {
                    Main.Log($"Blocked manual switch at fully damaged junction {key} (mode={mode}).");
                    return false; // Original überspringen -> kein Umschalten
                }
            }
            catch (Exception e)
            {
                Main.Log("AllowSwitch check failed: " + e, true);
            }
            return true;
        }
    }

    // Variante ohne Branch-Parameter
    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode) })]
    public static class Patch_Junction_Switch_Block_NoBranch
    {
        static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            => _JM_SwitchBlockHelper.AllowSwitch(__instance, mode);
    }

    // Variante mit Branch-Parameter
    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
    public static class Patch_Junction_Switch_Block_WithBranch
    {
        static bool Prefix(Junction __instance, Junction.SwitchMode mode, ref byte branch)
            => _JM_SwitchBlockHelper.AllowSwitch(__instance, mode);
    }

    // ------------------- DAMAGE ON FORCED RUN-THROUGH -------------------

    // Damage acquisition on forced switch-through
    [HarmonyPatch(typeof(Junction), nameof(Junction.Switch), new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
    public static class Patch_Junction_Switch_FORCED
    {
        static void Postfix(Junction __instance, Junction.SwitchMode mode, byte branch)
        {
            if (!Main.Enabled) return;
            try
            {
                if (mode != Junction.SwitchMode.FORCED) return;

                float speedKmh = SpeedEstimator.EstimateImpactSpeedKmh(__instance);
                int tier = Mathf.FloorToInt(speedKmh / 10f);
                float addPercent01 = tier <= 0 ? 0f : (tier / 100f); // 10 km/h -> +1%, 20 -> +2%, ...

                if (addPercent01 <= 0f) return;

                string key = DamageStore.MakeKey(__instance);
                DamageStore.AddPercent(key, addPercent01);

                // After a forced run-through, block random flips for a while
                FlipGuard.Block(key, Main.Settings.flipCooldownAfterForcedSec);

                float now = DamageStore.Get(key);
                Main.Log($"FORCED at {key} v={speedKmh:0.0} km/h -> +{addPercent01 * 100f:0.#}% damage, total {now * 100f:0.###}%");
            }
            catch (Exception e)
            {
                Main.Log("Exception in Switch Postfix: " + e, true);
            }
        }
    }

    // Random flip chance when coming from in-branch, proportional to (damage × multiplier/100)
    [HarmonyPatch(typeof(Junction), nameof(Junction.GetNextBranch))]
    public static class Patch_Junction_GetNextBranch
    {
        static void Postfix(Junction __instance, RailTrack currentTrack, bool first, ref Junction.Branch __result)
        {
            if (!Main.Enabled) return;

            try
            {
                var probe = new Junction.Branch(currentTrack, first);

                bool fromIn = (__instance?.inBranch != null &&
                               __instance.inBranch.track != null &&
                               __instance.inBranch.EqualsFields(probe));

                if (!fromIn) return;

                if (__instance?.outBranches != null)
                {
                    for (int i = 0; i < __instance.outBranches.Count; i++)
                    {
                        var ob = __instance.outBranches[i];
                        if (ob != null && ob.track != null && ob.EqualsFields(probe)) return;
                    }
                }

                string key = DamageStore.MakeKey(__instance);

                if (FlipGuard.IsBlocked(key)) return;

                float dmg01 = DamageStore.Get(key);
                if (dmg01 <= 0f) return;

                float speedKmh = SpeedEstimator.EstimateImpactSpeedKmh(__instance);
                if (speedKmh <= Main.Settings.safeNoFlipSpeedKmh)
                {
                    // Under safe speed, no randomness
                    return;
                }

                float multiplier = Mathf.Clamp(Main.Settings.flipMultiplierPercent, 1, 50) / 100f; // 0.01 .. 0.50
                float chance = dmg01 * multiplier; // e.g., 100% damage with 50 => 50% chance

                if (UnityEngine.Random.value <= chance)
                {
                    int count = __instance.outBranches != null ? __instance.outBranches.Count : 0;
                    if (count >= 2)
                    {
                        byte currentSel = __instance.selectedBranch;
                        byte newSel = currentSel;
                        for (int i = 0; i < 8; i++)
                        {
                            byte candidate = (byte)UnityEngine.Random.Range(0, count);
                            if (candidate != currentSel) { newSel = candidate; break; }
                        }

                        __result = __instance.outBranches[newSel];
                        Main.Log($"Random flip at {key}: chance {chance * 100f:0.#}% (damage {dmg01 * 100f:0.#}%, mult {multiplier * 100f:0.#}%), {currentSel} -> {newSel}");
                    }
                }
            }
            catch (Exception e)
            {
                Main.Log("Exception in GetNextBranch Postfix: " + e, true);
            }
        }
    }

    // ------------------- FAST TRAINCAR SNAPSHOT (no FindObjectsOfType) -------------------

    /// <summary>
    /// Schnelle, GC-arme Abfrage aller TrainCars ohne Szenensuche.
    /// Nutzt CarSpawner.Instance.AllCars und cached einen Snapshot für eine kurze TTL.
    /// </summary>
    public static class TrainCarQuery
    {
        private static TrainCar[] _snapshot = Array.Empty<TrainCar>();
        private static float _ts = -999f;
        private const float TTL_SEC = 0.20f; // 200 ms reichen hier völlig

        public static TrainCar[] GetAll()
        {
            float now = Time.unscaledTime;
            if (_snapshot == null || now - _ts > TTL_SEC)
            {
                var src = CarSpawner.Instance?.AllCars;
                if (src == null)
                {
                    _snapshot = Array.Empty<TrainCar>();
                }
                else
                {
                    // Manuell kopieren (ohne Linq), nulls filtern
                    int n = src.Count;
                    // worst-case: alle nicht-null
                    var buf = new TrainCar[n];
                    int k = 0;
                    for (int i = 0; i < n; i++)
                    {
                        var c = src[i];
                        if (c != null) buf[k++] = c;
                    }
                    if (k == n) _snapshot = buf;
                    else
                    {
                        _snapshot = new TrainCar[k];
                        for (int i = 0; i < k; i++) _snapshot[i] = buf[i];
                    }
                }
                _ts = now;
            }
            return _snapshot;
        }
    }

    public static class SpeedEstimator
    {
        /// <summary>
        /// Ermittelt die beste Annäherung der Fahrzeuggeschwindigkeit in der Nähe der Junction.
        /// Verwendet TrainCarQuery.GetAll() statt FindObjectsOfType.
        /// </summary>
        public static float EstimateImpactSpeedKmh(Junction j)
        {
            if (j == null) return 0f;
            Vector3 jp = j.transform.position;
            float bestD = 30f;
            float bestKmh = 0f;

            var cars = TrainCarQuery.GetAll();
            for (int i = 0; i < cars.Length; i++)
            {
                var tc = cars[i];
                var rb = tc?.rb;
                if (rb == null) continue;

                float d = Vector3.Distance(jp, tc.transform.position);
                if (d < bestD)
                {
                    bestD = d;
                    bestKmh = rb.velocity.magnitude * 3.6f;
                }
            }
            return bestKmh;
        }
    }
}
