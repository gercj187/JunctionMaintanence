// File: Main.cs
// Namespace: JunctionMaintanence
// Contains: Main (loader, GUI, update), repair helpers

using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;
using DV;

namespace JunctionMaintanence
{
    public class Main
    {
        public static UnityModManager.ModEntry Mod;
        public static Settings Settings;
        public static bool Enabled;
        private static Harmony _harmony;

        private static float _lastActionTs;
        private const float ACTION_COOLDOWN = 0.25f;

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;

            SelfHealSettingsFile(modEntry);

            try
            {
                Settings = Settings.Load<Settings>(modEntry);
            }
            catch (Exception ex)
            {
                Log("Settings.Load failed, creating defaults. " + ex, force: true);
                Settings = new Settings();
                try { Settings.Save(modEntry); } catch (Exception ex2) { Log("Settings.Save failed: " + ex2, force: true); }
            }

            modEntry.OnToggle  = OnToggle;
            modEntry.OnGUI     = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate  = OnUpdate;

            _harmony = new Harmony(modEntry.Info.Id);
            _harmony.PatchAll(typeof(Main).Assembly);

            Log("Loaded JunctionMaintanence.", force: true);
            return true;
        }

        private static void SelfHealSettingsFile(UnityModManager.ModEntry modEntry)
        {
            try
            {
                string path = Path.Combine(modEntry.Path, "Settings.xml");
                if (!File.Exists(path)) return;

                var fi = new FileInfo(path);
                bool bad = fi.Length < 8;
                if (!bad)
                {
                    string all = File.ReadAllText(path).TrimStart();
                    if (!all.StartsWith("<") || (!all.Contains("<Settings") && !all.Contains("<JunctionMaintanence.Settings")))
                        bad = true;
                }
                if (bad)
                {
                    string bak = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml";
                    File.Move(path, bak);
                    Log("Detected invalid Settings.xml. Moved to: " + bak + " and will regenerate defaults.", force: true);
                }
            }
            catch (Exception ex)
            {
                Log("SelfHealSettingsFile exception: " + ex, force: true);
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;
            Log("Toggled: " + (Enabled ? "ON" : "OFF"), force: true);
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Junction Maintanence - Damage Settings:", UnityModManager.UI.bold, GUILayout.ExpandWidth(false));
            GUILayout.Space(4);
            GUILayout.Label("Damage model (run-through): Damage in % = (speed kph / 10); <10 kph = no damage");
			Settings.enableRandomFlip = ToggleL("Enable random flip", Settings.enableRandomFlip);
			if (Settings.enableRandomFlip)
            {				
                // Safe-Speed (1..30 km/h, integer)
				GUILayout.BeginHorizontal();
				GUILayout.Label($"   Safe-Speed to avoid random flip : {Settings.safeNoFlipSpeedKmh:0} kph", GUILayout.Width(320));
                int safe = Mathf.RoundToInt(GUILayout.HorizontalSlider(Mathf.Clamp(Settings.safeNoFlipSpeedKmh, 1, 30), 1, 30, GUILayout.Width(300)));
                Settings.safeNoFlipSpeedKmh = Mathf.Clamp(safe, 1, 30);
				GUILayout.EndHorizontal();

                // Flip Chance-Multiplier (0.01 .. 0.50, step 0.01)
				GUILayout.BeginHorizontal();
				GUILayout.Label($"   Flip Chance-Multiplier: {Settings.flipMultiplierPercent:0.00} ×", GUILayout.Width(380));
                float f = Mathf.Round(GUILayout.HorizontalSlider(Mathf.Clamp01(Settings.flipMultiplierPercent), 0.01f, 0.50f, GUILayout.Width(300)) * 100f) / 100f;
				Settings.flipMultiplierPercent = Mathf.Clamp(f, 0.01f, 0.50f);
				GUILayout.EndHorizontal();
				
				// Examples
                GUILayout.Label("   Flip Examples:");
                GUILayout.Label($"   Junction with 1% Damage  = {(1f * Settings.flipMultiplierPercent):0.##}% chance");
                GUILayout.Label($"   Junction with 10% Damage = {(10f * Settings.flipMultiplierPercent):0.##}% chance");
                GUILayout.Label($"   Junction with 100% Damage = {(100f * Settings.flipMultiplierPercent):0.##}% chance");
            }
			
            Settings.BlockManualSwitchAtFullDamage = GUILayout.Toggle(Settings.BlockManualSwitchAtFullDamage,"Enable ruined junctions (cant be switched when 100% damaged)",GUILayout.Width(400));

            GUILayout.Space(10);
            GUILayout.Label("Junction Maintanence - Repair Settings:", UnityModManager.UI.bold, GUILayout.ExpandWidth(false));

            // Repair/List Radius (5..25 m integer)
			GUILayout.BeginHorizontal();
            GUILayout.Label($"Repair Radius:  {Settings.repairRadius:0} m", GUILayout.Width(320));
            float rr = GUILayout.HorizontalSlider(Mathf.Round(Mathf.Clamp(Settings.repairRadius, 5f, 25f)), 5f, 25f, GUILayout.Width(300));
            Settings.repairRadius = Mathf.Round(Mathf.Clamp(rr, 5, 25));
            GUILayout.EndHorizontal();

            // Max Repair Cost
            Settings.maxRepairCostFull = FloatFieldL("Max Repair Cost: $", Settings.maxRepairCostFull);
			
			/*
			================= OLD SETTINGS =================
            GUILayout.Space(6);
            GUILayout.Label("Repair Settings", UnityModManager.UI.bold, GUILayout.ExpandWidth(false));

            // Repair/List Radius (5..25 m integer)
            

            Settings.repairKey                  = TextFieldL("Repair hotkey (letter)", Settings.repairKey);
            Settings.repairAmountPercent        = FloatFieldL("Repair amount per action (0..1)", Settings.repairAmountPercent);
            Settings.repairVehicleSearchRadius  = FloatFieldL("Vehicle proximity radius (m)", Settings.repairVehicleSearchRadius);
            Settings.maxVehicleStandingSpeedKmh = FloatFieldL("Max vehicle speed for repair (km/h)", Settings.maxVehicleStandingSpeedKmh);
            

            GUILayout.Space(6);
            GUILayout.Label("Flip Behavior", UnityModManager.UI.bold, GUILayout.ExpandWidth(false));
            Settings.flipCooldownAfterForcedSec = FloatFieldL("Cooldown after forced run-through (s)", Settings.flipCooldownAfterForcedSec);
			================= OLD SETTINGS =================
			*/
            GUILayout.Space(6);
            Settings.logging = ToggleL("Enable logging", Settings.logging);
            if (GUILayout.Button("List damaged junctions in log", GUILayout.Width(260)))
            {
                foreach (var kv in DamageStore.All())
                    Log($"Damaged: {kv.Key} -> {kv.Value * 100f:0.###}%");
            }
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry);
            Log("Settings saved.", force: true);
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            if (!Enabled) return;

            // Robust: ensure save-data loaded once, in case early hook was missed
            JM_SaveRuntime.EnsureLoadedFromSaveOnce();

            if (string.IsNullOrEmpty(Settings.repairKey)) return;

            try
            {
                // If price > 0 : hotkey repair disabled (payment only via Career Manager)
                if (Settings.maxRepairCostFull > 0.001f) return;

                if (UnityEngine.Input.anyKey && Time.unscaledTime - _lastActionTs > ACTION_COOLDOWN)
                {
                    foreach (char c in UnityEngine.Input.inputString)
                    {
                        if (char.ToLowerInvariant(c) == char.ToLowerInvariant(Settings.repairKey[0]))
                        {
                            _lastActionTs = Time.unscaledTime;
                            Log("Repair hotkey detected. Attempting free repair.", force: true);
                            TryVehicleBoundRepair();
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log("OnUpdate exception: " + e, force: true);
            }
        }

        // --- Repair and search helpers (free repair path for maxRepairCostFull==0) ---

        private static void TryVehicleBoundRepair()
        {
            var player = Camera.main != null ? Camera.main.transform : null;
            if (player == null)
            {
                Log("Repair aborted: No player camera found.");
                return;
            }

            TrainCar vehicle = FindNearestCaboose(player.position, Settings.repairVehicleSearchRadius, Settings.maxVehicleStandingSpeedKmh);
            if (vehicle == null) { Log("Repair aborted: No Caboose nearby (standing)."); return; }

            float jr = Mathf.Max(1f, Settings.repairRadius);
            Junction nearest = FindNearestJunction(vehicle.transform.position, jr);
            if (nearest == null) nearest = FindNearestJunction(player.position, jr);
            if (nearest == null)
            {
                Log($"No junction within {jr:0.0} m.");
                return;
            }

            string key   = DamageStore.MakeKey(nearest);
            float before = DamageStore.Get(key);
            if (before <= 0f)
            {
                Log($"Junction {key} has no damage.");
                return;
            }

            float step = Mathf.Clamp01(Settings.repairAmountPercent);
            float repaired01 = Mathf.Min(step, before);
            float after = Mathf.Max(0f, before - repaired01);
            DamageStore.Set(key, after);
            Log($"Repaired junction {key} by {repaired01 * 100f:0.#}% (free) -> {after * 100f:0.###}% remaining.");
        }

        private static Junction FindNearestJunction(Vector3 center, float radius)
        {
            Junction nearest = null;
            float best = float.MaxValue;
            foreach (var j in GameObject.FindObjectsOfType<Junction>())
            {
                float d = Vector3.Distance(center, j.transform.position);
                if (d <= radius && d < best)
                {
                    nearest = j;
                    best = d;
                }
            }
            return nearest;
        }

        private static TrainCar FindNearestCaboose(Vector3 playerPos, float radius, float maxStandingKmh)
        {
            float rSqr = radius * radius;

            TrainCar targetCar = CarSpawner.Instance.AllCars
                .FirstOrDefault(car => car != null &&
                                       car.name != null &&
                                       car.name.IndexOf("caboose", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                       (car.transform.position - playerPos).sqrMagnitude <= rSqr);

            if (targetCar == null) return null;

            // standing?
            float kmh = RbSpeedKmh(targetCar.rb);
            if (kmh > Mathf.Max(0.1f, maxStandingKmh)) return null;

            return targetCar;
        }

        public static float RbSpeedKmh(Rigidbody rb)
        {
            if (rb == null) return 0f;
            return rb.velocity.magnitude * 3.6f;
        }

        // --- Logging and UI helpers ---

        public static void Log(string msg, bool force = false)
        {
            if (!force && Settings != null && !Settings.logging) return;
            Mod?.Logger.Log("[JunctionMaintanence] " + msg);
        }

        private static float FloatFieldL(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(320));
            string s = GUILayout.TextField(value.ToString(System.Globalization.CultureInfo.InvariantCulture), GUILayout.Width(120));
            GUILayout.EndHorizontal();
            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                return f;
            return value;
        }

        private static string TextFieldL(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(320));
            string s = GUILayout.TextField(value ?? string.Empty, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            return s;
        }

        private static bool ToggleL(string label, bool value)
        {
            GUILayout.BeginHorizontal();
            bool v = GUILayout.Toggle(value, label, GUILayout.Width(320));
            GUILayout.EndHorizontal();
            return v;
        }
    }
}
