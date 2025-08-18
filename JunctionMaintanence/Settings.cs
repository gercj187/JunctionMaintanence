// File: Settings.cs
// Namespace: JunctionMaintanence
// Contains: Settings, DamageEntry, DamageStore, FlipGuard, JM_Save, JM_SaveRuntime

using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using UnityEngine;
using UnityModManagerNet;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using DV;

namespace JunctionMaintanence
{
    [Serializable]
    public class Settings : UnityModManager.ModSettings
    {
        public bool logging = false;

        // Flip config
        public bool enableRandomFlip = true;
		public float safeNoFlipSpeedKmh = 10.0f;
        public float flipMultiplierPercent = 0.15f; // 0.01..0.50
        public float flipCooldownAfterForcedSec = 5.0f;
		
        // Repair config
        public string repairKey = "j";
        public float repairRadius = 10f;
        public float repairAmountPercent = 1f;
        public float repairVehicleSearchRadius = 12f;
        public float maxVehicleStandingSpeedKmh = 1.0f;
        public float maxRepairCostFull = 1000f;        // $ cost for 100% repair (wallet pay)
		public bool BlockManualSwitchAtFullDamage = true;


        [XmlIgnore]
        private Dictionary<string, float> _damageMap = new Dictionary<string, float>(1024);

        [XmlIgnore]
        public Dictionary<string, float> DamageMap => _damageMap;

    }

    [Serializable]
    public class DamageEntry
    {
        [XmlAttribute("k")]
        public string Key;

        [XmlAttribute("v")]
        public float Value;
    }

    // Runtime damage map accessors (shared)
    public static class DamageStore
    {
        public static float Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0f;
            if (Main.Settings.DamageMap.TryGetValue(key, out var v)) return Mathf.Clamp01(v);
            return 0f;
        }

        public static void AddPercent(string key, float percent01)
        {
            if (string.IsNullOrEmpty(key)) return;
            float cur = Get(key);
            float next = Mathf.Clamp01(cur + Mathf.Max(0f, percent01));
            Main.Settings.DamageMap[key] = next;
        }

        public static void Set(string key, float value01)
        {
            if (string.IsNullOrEmpty(key)) return;
            Main.Settings.DamageMap[key] = Mathf.Clamp01(value01);
        }

        public static Dictionary<string, float> All() => Main.Settings.DamageMap;

        public static void ReplaceAll(IDictionary<string, float> newMap)
        {
            Main.Settings.DamageMap.Clear();
            foreach (var kv in newMap)
                Main.Settings.DamageMap[kv.Key] = Mathf.Clamp01(kv.Value);
        }

        public static string MakeKey(Junction j)
		{
			if (j == null) return "NULL";

			// NUR den LongID-Namen der Weiche nehmen
			string id = (j.junctionData.junctionIdLong ?? "X").Trim();

			if (string.IsNullOrEmpty(id))
			{
				// Fallback: GameObject-Name verwenden
				id = j?.gameObject?.name ?? "Switch";
			}

			return id;
		}

        private static int HashPositionXZ(Vector3 p)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(p.x * 10f);
                h = h * 31 + Mathf.RoundToInt(p.z * 10f);
                return h;
            }
        }
    }

    // Flip suppression window after forced run-through
    public static class FlipGuard
    {
        private static readonly Dictionary<string, float> blockUntil = new Dictionary<string, float>(256);

        public static void Block(string key, float seconds)
        {
            if (string.IsNullOrEmpty(key)) return;
            blockUntil[key] = Time.unscaledTime + Mathf.Max(0f, seconds);
        }

        public static bool IsBlocked(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!blockUntil.TryGetValue(key, out var t)) return false;
            return Time.unscaledTime < t;
        }
    }

    // --- SAVE/LOAD helpers serialized into SaveGameManager.data.dataObject ---
    internal static class JM_Save
    {
        public const string KEY = "JunctionMaintanence_Map";

        public static JArray ToJsonArray(IDictionary<string, float> map)
        {
            var arr = new JArray();
            if (map == null || map.Count == 0) return arr;
            foreach (var kv in map)
            {
                var o = new JObject
                {
                    ["Junction"] = kv.Key,
                    ["Damage"] = Mathf.Clamp01(kv.Value)
                };
                arr.Add(o);
            }
            return arr;
        }

        public static Dictionary<string, float> FromToken(JToken token)
        {
            var result = new Dictionary<string, float>(1024);
            if (token == null || token.Type == JTokenType.Null) return result;

            if (token.Type == JTokenType.Array)
            {
                foreach (var t in (JArray)token)
                {
                    var key = (string)t["Junction"];
                    var valTok = t["Damage"];
                    if (string.IsNullOrEmpty(key) || valTok == null) continue;
                    float v = 0f;
                    try { v = valTok.ToObject<float>(); } catch { }
                    result[key] = Mathf.Clamp01(v);
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                foreach (var prop in ((JObject)token).Properties())
                {
                    float v = 0f;
                    try { v = prop.Value.ToObject<float>(); } catch { }
                    result[prop.Name] = Mathf.Clamp01(v);
                }
            }
            return result;
        }
    }

    // Optional robust: Lazy-load once during runtime (in case MakeCurrent was missed)
    internal static class JM_SaveRuntime
    {
        private static bool loadedOnce = false;

        public static void EnsureLoadedFromSaveOnce()
        {
            if (loadedOnce) return;

            try
            {
                var data = SaveGameManager.Instance?.data;
                if (data == null) return;

                var obj = Traverse.Create(data).Field("dataObject").GetValue<JObject>();
                if (obj == null) return;

                if (obj.TryGetValue(JM_Save.KEY, out JToken tok) && tok != null && tok.Type != JTokenType.Null)
                {
                    var map = JM_Save.FromToken(tok);
                    DamageStore.ReplaceAll(map);
                    loadedOnce = true;
                    Main.Log($"Lazy-load: restored {map.Count} junction damage entries.");
                }
            }
            catch (Exception e)
            {
                Main.Log("EnsureLoadedFromSaveOnce error: " + e, true);
            }
        }
    }
}
