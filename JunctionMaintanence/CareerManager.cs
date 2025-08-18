// File: CareerManager.cs
// Namespace: JunctionMaintanence

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using TMPro;
using DV;
using DV.ServicePenalty.UI;

namespace JunctionMaintanence
{
    // ---------- Hilfen ----------
    internal static class JM_CareerManagerHelpers
    {
        private static List<Junction> _junctionCache = null;
        private static float _junctionCacheTs = -999f;
        private const float CACHE_TTL_SEC = 10f;

        private static void EnsureJunctionCache()
        {
            if (_junctionCache != null && Time.unscaledTime - _junctionCacheTs < CACHE_TTL_SEC) return;
            try
            {
                _junctionCache = new List<Junction>(GameObject.FindObjectsOfType<Junction>());
                _junctionCacheTs = Time.unscaledTime;
                Main.Log($"[CM] Junction cache rebuilt: count={_junctionCache.Count}");
            }
            catch (Exception e)
            {
                Main.Log("EnsureJunctionCache error: " + e, true);
                if (_junctionCache == null) _junctionCache = new List<Junction>();
            }
        }

        public static List<(Junction j, float dSqr)> GetAllSortedWithin(Vector3 center, float radius)
        {
            EnsureJunctionCache();
            float rSqr = radius * radius;
            var list = new List<(Junction, float)>(32);

            foreach (var j in _junctionCache)
            {
                if (j == null) continue;
                float dSqr = (j.transform.position - center).sqrMagnitude;
                if (dSqr <= rSqr) list.Add((j, dSqr));
            }

            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return list;
        }

        private static string GetJunctionName(Junction j)
        {
            if (j == null) return "Switch";
            try
            {
                string idLong = j.junctionData.junctionIdLong;
                if (!string.IsNullOrWhiteSpace(idLong)) return idLong;
            }
            catch { /* fallback unten */ }

            try
            {
                var goName = j.gameObject != null ? j.gameObject.name : null;
                return string.IsNullOrWhiteSpace(goName) ? "Switch" : goName;
            }
            catch
            {
                return "Switch";
            }
        }

        /// <summary>
        /// [JM Station Filter] Liefert den "logischen" Anzeigenamen (GameObject-/LongID-Name).
        /// </summary>
        public static string GetDisplayName(Junction j) => GetJunctionName(j);

        /// <summary>
        /// Liest den persistierten Schaden (0..1) für die Junction.
        /// </summary>
        public static float GetPersistentDamage01(Junction j)
        {
            try
            {
                // Save-Daten in Runtime-Store sicherstellen
                JM_SaveRuntime.EnsureLoadedFromSaveOnce();

                // Stabiler Key (bevorzugt junctionIdLong)
                string key = DamageStore.MakeKey(j);

                // Abfrage aus DamageStore (save-gebunden)
                return Mathf.Clamp01(DamageStore.Get(key));
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// Berechnet die voraussichtlichen Kosten ($) für den nächsten Reparaturschritt
        /// basierend auf aktuellem Schaden und Settings (repairAmountPercent * maxRepairCostFull).
        /// </summary>
        private static double ComputeNextStepCost(Junction j)
        {
            string key = DamageStore.MakeKey(j);
            float before = Mathf.Clamp01(DamageStore.Get(key)); // 0..1
            float step = Mathf.Clamp01(Main.Settings.repairAmountPercent);
            float repaired01 = Mathf.Min(step, before);
            double cost = Math.Round(Math.Max(0.0, Main.Settings.maxRepairCostFull) * repaired01, 2);
            return cost;
        }

        /// <summary>
        /// Baut die formatierte Listenzeile. Optional Kosten ausblenden (Stationsmodus).
        /// </summary>
        public static string FormatLine(Junction j, float _dSqrIgnored, bool showCost = true)
        {
            const int TOTAL_WIDTH = 53;

            string nameCol = GetJunctionName(j) ?? "Junction";

            float dmgPctF = GetPersistentDamage01(j) * 100f;
            int dmgPct = Mathf.RoundToInt(dmgPctF);
            string dmgCol = $"DMG = {dmgPct} %";

            string costCol = "";
            if (showCost)
            {
                double cost = ComputeNextStepCost(j);
                costCol = cost.ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " $";
            }

            var sb = new StringBuilder(new string(' ', TOTAL_WIDTH));

            // Name links
            for (int i = 0; i < nameCol.Length && i < TOTAL_WIDTH; i++)
                sb[i] = nameCol[i];

            // DMG mittig
            int center = (TOTAL_WIDTH / 2) - (dmgCol.Length / 2);
            for (int i = 0; i < dmgCol.Length && center + i < TOTAL_WIDTH; i++)
                sb[center + i] = dmgCol[i];

            if (showCost && !string.IsNullOrEmpty(costCol))
            {
                // Kosten ganz rechts
                int right = TOTAL_WIDTH - costCol.Length;
                for (int i = 0; i < costCol.Length && right + i < TOTAL_WIDTH; i++)
                    sb[right + i] = costCol[i];
            }

            return sb.ToString();
        }

        public static void GetMenuColors(DisplayScreenSwitcher sw, out Color regular, out Color highlighted)
        {
            regular = Color.white;
            highlighted = new Color(1f, 0.8f, 0.3f);

            if (sw == null) return;
            try
            {
                var t = sw.GetType();
                var fReg = t.GetField("REGULAR_COLOR", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                var fHL  = t.GetField("HIGHLIGHTED_COLOR", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

                if (fReg != null)
                {
                    var v = fReg.IsStatic ? fReg.GetValue(null) : fReg.GetValue(sw);
                    if (v is Color c) regular = c;
                }
                if (fHL != null)
                {
                    var v = fHL.IsStatic ? fHL.GetValue(null) : fHL.GetValue(sw);
                    if (v is Color c) highlighted = c;
                }
            }
            catch { }
        }

        private static readonly MethodInfo _miSetInfo =
            AccessTools.Method(typeof(CareerManagerInfoScreen), "SetInfoData",
                new[] { typeof(IDisplayScreen), typeof(string), typeof(string),
                        typeof(string), typeof(string), typeof(string), typeof(string), typeof(Action) });

        /// <summary>
        /// Klassische 4-Zeilen-Variante (falls irgendwo noch gebraucht).
        /// </summary>
        public static void SetInfo(CareerManagerInfoScreen info, IDisplayScreen returnScreen,
                                   string title, string paragraph, string l1, string l2, string l3, string l4)
        {
            try
            {
                _miSetInfo?.Invoke(info, new object[] { returnScreen, title, paragraph, l1, l2, l3, l4, null });
                info.Activate(returnScreen);
            }
            catch (Exception e)
            {
                Main.Log("SetInfo invoke failed: " + e, true);
            }
        }

        /// <summary>
        /// [JM List N-rows] Generische Variante: Wir rendern beliebig viele Zeilen im Paragraph-Feld.
        /// Die L1..L4 bleiben leer, damit die Paragraph-Fläche maximalen Platz hat.
        /// </summary>
        public static void SetInfoLines(CareerManagerInfoScreen info, IDisplayScreen returnScreen,
                                        string title, IList<string> lines)
        {
            try
            {
                string paragraph = (lines == null || lines.Count == 0)
                    ? string.Empty
                    : string.Join("\n", lines);

                _miSetInfo?.Invoke(info, new object[] { returnScreen, title, paragraph, "", "", "", "", null });
                info.Activate(returnScreen);
            }
            catch (Exception e)
            {
                Main.Log("SetInfoLines invoke failed: " + e, true);
            }
        }

        public static void SetTMPText(Component comp, string txt)
        {
            if (comp == null) return;
            try
            {
                var prop = comp.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null) prop.SetValue(comp, txt);
            }
            catch { }
        }

        /// Prüft ausschließlich das eigene Fahrzeug (TrainCar) auf Stillstand.
        /// true = steht (oder kein Fahrzeug vorhanden); false = in Bewegung.
        public static bool IsStandingSelf(TrainCar selfCar)
        {
            float maxKmh = Mathf.Max(0.1f, Main.Settings.maxVehicleStandingSpeedKmh);
            if (selfCar == null || selfCar.rb == null) return true; // kein Fahrzeug → behandeln wir als "steht"
            float kmh = selfCar.rb.velocity.magnitude * 3.6f;
            return kmh <= maxKmh;
        }

        /// Findet robust das eigene Fahrzeug:
        /// 1) Parent-TrainCar des Host-Transforms, sonst
        /// 2) nächster Caboose innerhalb 12 m (für CareerManagerTrainInterior).
        public static TrainCar ResolveSelfCar(Transform hostTf)
        {
            if (hostTf == null) return null;

            // 1) Try direct parent chain
            var tc = hostTf.GetComponentInParent<TrainCar>();
            if (tc != null) return tc;

            // 2) Fallback: nearest caboose around host (≤ 12 m)
            var all = CarSpawner.Instance?.AllCars;
            if (all == null) return null;

            Vector3 p = hostTf.position;
            float bestD2 = 12f * 12f;
            TrainCar best = null;

            int n = all.Count;
            for (int i = 0; i < n; i++)
            {
                var c = all[i];
                if (c == null) continue;

                string nm = c.name ?? "";
                if (nm.IndexOf("caboose", StringComparison.OrdinalIgnoreCase) < 0) continue;

                float d2 = (c.transform.position - p).sqrMagnitude;
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = c;
                }
            }
            return best;
        }

        // -------------------- [JM Station Filter] Helpers --------------------
		
        // Muster: S-<Ziffern oder alphanum>-<STATION>
        private static readonly Regex RxStationSwitch = new Regex(@"^S-[^-]+-([A-Z]{1,4})$", RegexOptions.Compiled);

        /// <summary>
        /// Versucht anhand der umgebenden Weichen (S-*-CODE) den Stationscode zu ermitteln.
        /// </summary>
        public static string InferStationCode(Vector3 center, float probeRadius = 500f)
        {
            EnsureJunctionCache();
            var hist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var j in _junctionCache)
            {
                if (j == null) continue;
                float d = Vector3.Distance(center, j.transform.position);
                if (d > probeRadius) continue;

                string name = GetDisplayName(j);
                if (string.IsNullOrEmpty(name)) continue;

                // Nur S-*-<CODE>
                var m = RxStationSwitch.Match(name);
                if (!m.Success) continue;

                string code = m.Groups[1].Value;
                if (string.IsNullOrEmpty(code)) continue;

                hist.TryGetValue(code, out int c);
                hist[code] = c + 1;
            }

            if (hist.Count == 0) return null;

            // Mehrheit wählen
            string best = null;
            int bestC = -1;
            foreach (var kv in hist)
            {
                if (kv.Value > bestC) { best = kv.Key; bestC = kv.Value; }
            }
            return best;
        }

        /// <summary>
        /// Liefert ALLE Weichen der Station (S-*-<stationCode>), nach Name sortiert. dSqr nach Distanz befüllt.
        /// </summary>
        public static List<(Junction j, float dSqr)> GetStationSwitches(string stationCode, Vector3 center)
        {
            EnsureJunctionCache();
            var list = new List<(Junction, float)>(64);
            if (string.IsNullOrEmpty(stationCode)) return list;

            foreach (var j in _junctionCache)
            {
                if (j == null) continue;
                string name = GetDisplayName(j);
                if (string.IsNullOrEmpty(name)) continue;

                // exakt: Start mit 'S-' und Ende mit '-<CODE>'
                if (!name.StartsWith("S-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith("-" + stationCode, StringComparison.OrdinalIgnoreCase)) continue;

                float dSqr = (j.transform.position - center).sqrMagnitude;
                list.Add((j, dSqr));
            }

            // nach Name, dann Distanz
            list.Sort((a, b) =>
            {
                string an = GetDisplayName(a.Item1);
                string bn = GetDisplayName(b.Item1);
                int c = string.CompareOrdinal(an, bn);
                if (c != 0) return c;
                return a.Item2.CompareTo(b.Item2);
            });

            return list;
        }
    }

    // ---------- Listen-Zustand ----------
    internal static class JM_CM_ListState
    {
        public static bool Active;
        public static List<(Junction j, float dSqr)> Items = new List<(Junction, float)>(32);
        public static int Selected;
        public static int Top;
        public static CareerManagerInfoScreen Info;
        public static CareerManagerMainScreen MainScreen;
        public static Vector3 Center;
        public static float Radius;
        public static bool NoDamagePromptActive;
        public static bool StopPromptActive;

        // [JM Station Filter]
        public static bool StationMode;           // true: wir sind am CareerManager einer Station
        public static string StationCode;         // z. B. "SM"

        internal const int VISIBLE = 10; // <<<<<<<<<< 10 sichtbare Zeilen
        private const string TITLE = "JUNCTION MAINTANENCE";

        public static void Start(CareerManagerMainScreen main, CareerManagerInfoScreen info, Vector3 center)
        {
            JM_SaveRuntime.EnsureLoadedFromSaveOnce();

            MainScreen = main;
            Info = info;
            Center = center;
            Radius = Mathf.Round(Mathf.Clamp(JunctionMaintanence.Main.Settings.repairRadius, 5f, 25f));

            // Host ermitteln und prüfen, ob wir mobil (Caboose) sind
            var host = (main?.screenSwitcher != null) ? main.screenSwitcher.transform : main?.transform;

            bool isMobileCareerManager = host != null &&
                host.name.Equals("CareerManagerTrainInterior", StringComparison.OrdinalIgnoreCase);

            Main.Log($"[CM] Host={host?.name ?? "NULL"}  isMobile={isMobileCareerManager}");

            if (isMobileCareerManager)
            {
                // CABOOSE: Radius-Modus (alles anzeigen + reparieren)
                StationMode = false;
                StationCode = null;
                Items = JM_CareerManagerHelpers.GetAllSortedWithin(Center, Radius);
            }
            else
            {
                // STATION: nur S-*-<CODE> (ohne Kosten/Interaktion)
                StationCode = JM_CareerManagerHelpers.InferStationCode(Center, 500f);
                StationMode = !string.IsNullOrEmpty(StationCode);

                if (StationMode)
                    Items = JM_CareerManagerHelpers.GetStationSwitches(StationCode, Center);
                else
                    Items = JM_CareerManagerHelpers.GetAllSortedWithin(Center, Radius);
            }

            Selected = 0;
            Top = 0;
            Active = true;

            Render();
            MainScreen.screenSwitcher.SetActiveDisplay(Info);
        }

        public static void Stop()
        {
            Active = false;
            Items.Clear();
            Info = null;
            MainScreen = null;
            Selected = Top = 0;
            NoDamagePromptActive = false;
            StopPromptActive = false;
            StationMode = false;
            StationCode = null;
        }

        public static void MoveSelection(int delta)
        {
            if (!Active || Items.Count == 0) return;

            Selected = Mathf.Clamp(Selected + delta, 0, Items.Count - 1);
            if (Selected < Top) Top = Selected;
            if (Selected > Top + (VISIBLE - 1)) Top = Selected - (VISIBLE - 1);

            int maxTop = Mathf.Max(0, Items.Count - VISIBLE);
            if (Top > maxTop) Top = maxTop;

            Render();
        }

        public static void Render()
        {
            if (!Active || Info == null) return;

            JM_SaveRuntime.EnsureLoadedFromSaveOnce();

            // Station vs. Caboose strikt nach StationMode
            if (StationMode)
            {
                Items = JM_CareerManagerHelpers.GetStationSwitches(StationCode, Center); // keine Kosten
            }
            else
            {
                Items = JM_CareerManagerHelpers.GetAllSortedWithin(Center, Radius);      // Caboose/Radius
            }

            int count = Items.Count;

            string title = StationMode ? "Station Junction Overview" : "Mobile Junction Maintenance";

            JM_CareerManagerHelpers.GetMenuColors(MainScreen?.screenSwitcher, out var reg, out var hl);

            string L(int idx)
            {
                if (idx < 0 || idx >= count) return "";
                // Im StationMode KEINE Kosten anzeigen:
                string line = JM_CareerManagerHelpers.FormatLine(Items[idx].j, Items[idx].dSqr, showCost: !StationMode);
                bool isSel = (idx == Selected);
                var col = isSel ? hl : reg;
                string hex = ColorUtility.ToHtmlStringRGB(col);
                return $"<color=#{hex}>{line}</color>";
            }

            // Bis zu VISIBLE Zeilen vorbereiten
            var lines = new List<string>(VISIBLE);
            if (count == 0)
            {
                lines.Add(StationMode ? "No station switches found." : "No switches nearby.");
            }
            else
            {
                for (int i = 0; i < VISIBLE; i++)
                {
                    lines.Add(L(Top + i));
                }
            }

            // N-Zeilen Rendering im Paragraph
            JM_CareerManagerHelpers.SetInfoLines(Info, MainScreen, title, lines);
        }
    }

    // ---------- Menüeintrag ergänzen ----------
    [HarmonyPatch(typeof(CareerManagerMainScreen), "Awake")]
    internal static class JM_CM_Awake_AddMenuItem
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                var fSelectable = AccessTools.Field(typeof(CareerManagerMainScreen), "selectableText");
                var arrObj = fSelectable.GetValue(__instance) as Array;
                if (arrObj == null || arrObj.Length < 4) return; // unerwartetes Layout
                if (arrObj.Length >= 5) return;                   // Eintrag schon vorhanden

                var statsComp = arrObj.GetValue(3) as Component;
                var ownedComp = arrObj.GetValue(2) as Component;
                if (statsComp == null || ownedComp == null) return;

                // UI-Element klonen
                var parent = statsComp.transform.parent;
                var cloneGO = UnityEngine.Object.Instantiate(statsComp.gameObject, parent);
                cloneGO.name = "RepairSwitchEntry";

                // TMP-Komponente ermitteln und Label setzen
                var elementType = arrObj.GetType().GetElementType();
                var tmpComp = cloneGO.GetComponent(elementType) as Component;
                string initialLabel = "Junction Maintenance"; // Main-Menütext
                try { elementType.GetProperty("text")?.SetValue(tmpComp, initialLabel); } catch { }

                // Position: eine Zeile unter "Stats"
                var statsPos = statsComp.transform.localPosition;
                var ownedPos = ownedComp.transform.localPosition;
                var lineStep = statsPos - ownedPos;
                cloneGO.transform.localPosition = statsPos + lineStep;

                // Array auf 5 Einträge erweitern und neues Element einhängen
                var newArr = Array.CreateInstance(elementType, 5);
                for (int i = 0; i < 4; i++) newArr.SetValue(arrObj.GetValue(i), i);
                newArr.SetValue(tmpComp, 4);
                fSelectable.SetValue(__instance, newArr);

                // Selector und Slot-Anzahl anpassen
                AccessTools.Field(typeof(ScrollableDisplayScreen), "activeSlotCount").SetValue(__instance, 5);
                var newSelector = new IntIterator(0, 5, isWrappable: true);
                AccessTools.Field(typeof(ScrollableDisplayScreen), "selector").SetValue(__instance, newSelector);
            }
            catch (Exception e)
            {
                Main.Log("CM Awake inject failed: " + e, true);
            }
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), "Activate")]
    internal static class JM_CM_Activate_Label
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                var fSelectable = AccessTools.Field(typeof(CareerManagerMainScreen), "selectableText");
                var arr = fSelectable.GetValue(__instance) as Array;
                if (arr != null && arr.Length >= 5)
                {
                    if (arr.GetValue(4) is Component c)
                    {
                        var prop = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                        prop?.SetValue(c, "Junction Maintenance");
                    }
                }
            }
            catch (Exception e) { Main.Log("CM Activate inject failed: " + e, true); }
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), "Disable")]
    internal static class JM_CM_Disable_ClearClone
    {
        static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                var fSelectable = AccessTools.Field(typeof(CareerManagerMainScreen), "selectableText");
                var arr = fSelectable.GetValue(__instance) as Array;
                if (arr != null && arr.Length >= 5)
                {
                    if (arr.GetValue(4) is Component c)
                    {
                        var prop = c.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                        prop?.SetValue(c, string.Empty);
                    }
                }
            }
            catch (Exception e) { Main.Log("CM Disable clear failed: " + e, true); }
        }
    }

    // ---------- Main -> Liste öffnen ----------
    [HarmonyPatch(typeof(CareerManagerMainScreen), "HandleInputAction")]
    internal static class JM_CM_HandleInput_ConfirmOpen
    {
        static bool Prefix(CareerManagerMainScreen __instance, InputAction input)
        {
            try
            {
                if (input != InputAction.Confirm) return true;

                var sel = (IntIterator)AccessTools.Field(typeof(ScrollableDisplayScreen), "selector").GetValue(__instance);
                if (sel == null || sel.Current != 4) return true;

                // Mittelpunkt für Reichweiten- und Geschwindigkeitsprüfung
				var center = (__instance.screenSwitcher != null) ? __instance.screenSwitcher.transform.position : __instance.transform.position;

				// Eigenes Fahrzeug bestimmen (Caboose/TrainInterior hat einen übergeordneten TrainCar)
				var hostTf  = (__instance.screenSwitcher != null) ? __instance.screenSwitcher.transform : __instance.transform;
                var selfCar = JM_CareerManagerHelpers.ResolveSelfCar(hostTf);

				// Nur das eigene Fahrzeug betrachten
				if (!JM_CareerManagerHelpers.IsStandingSelf(selfCar))
				{
					var info = __instance.infoScreen;
					if (info != null)
					{
						JM_CM_ListState.Info = info;
						JM_CM_ListState.MainScreen = __instance;
						JM_CM_ListState.StopPromptActive = true;

						JM_CareerManagerHelpers.SetInfoLines(info, __instance, "JUNCTION MAINTANENCE",
							new[] { "Stop the train to access maintenance." });
						__instance.screenSwitcher?.SetActiveDisplay(info);
					}
					return false;
				}

                JM_CM_ListState.Start(__instance, __instance.infoScreen, center);
                JM_SaveRuntime.EnsureLoadedFromSaveOnce();
                return false;
            }
            catch (Exception e)
            {
                Main.Log("CM HandleInput (open) failed: " + e, true);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(CareerManagerInfoScreen), "Disable")]
    internal static class JM_Info_Disable_ResetStopPrompt
    {
        static void Postfix()
        {
            // StopPrompt-Flag immer räumen
            JM_CM_ListState.StopPromptActive = false;

            // WICHTIG: Beim Wechsel zum Pay-Screen ist JM_Payment.Active == true.
            // In diesem Fall NICHT den Listen-State verwerfen, sonst sind
            // Info/MainScreen-Referenzen weg und Cancel kann nicht zurückschalten.
            if (!JM_Payment.Active)
            {
                JM_CM_ListState.Stop();
            }
        }
    }

    // ---------- Listeingaben + Wechsel auf License-PayScreen ----------
    [HarmonyPatch(typeof(CareerManagerInfoScreen), "HandleInputAction")]
    internal static class JM_Info_HandleInput_Scrolling
    {
        static bool Prefix(CareerManagerInfoScreen __instance, InputAction input)
        {
            // "Stop the train"-Screen — blockiere ALLE Eingaben außer Cancel.
            if (JM_CM_ListState.StopPromptActive && ReferenceEquals(__instance, JM_CM_ListState.Info))
            {
                if (input == InputAction.Cancel)
                {
                    JM_CM_ListState.StopPromptActive = false;
                    var sw = __instance.screenSwitcher;
                    IDisplayScreen target = (JM_CM_ListState.MainScreen as IDisplayScreen)
                                            ?? (JM_CM_ListState.Info as IDisplayScreen)
                                            ?? __instance;
                    sw?.SetActiveDisplay(target);
                }
                return false; // alles schlucken
            }

            if (!JM_CM_ListState.Active || !ReferenceEquals(__instance, JM_CM_ListState.Info))
                return true;

            switch (input)
            {
                case InputAction.Up:
                    JM_CM_ListState.MoveSelection(-1);
                    return false;

                case InputAction.Down:
                    JM_CM_ListState.MoveSelection(+1);
                    return false;

                case InputAction.Confirm:
                {
                    if (JM_CM_ListState.StationMode) return false; // in Stationen keine Interaktion

                    if (JM_CM_ListState.Items.Count == 0) return false;

                    var (j, _) = JM_CM_ListState.Items[JM_CM_ListState.Selected];
                    if (j == null) return false;

                    string key = DamageStore.MakeKey(j);
                    float before = DamageStore.Get(key); // 0..1
                    float step   = Mathf.Clamp01(Main.Settings.repairAmountPercent);
                    float repaired01 = Mathf.Min(step, before);

                    if (repaired01 <= 0f)
                    {
                        JM_CM_ListState.NoDamagePromptActive = true;
                        // Meldung über Paragraph (generisch)
                        JM_CareerManagerHelpers.SetInfoLines(JM_CM_ListState.Info, JM_CM_ListState.Info,
                            "JUNCTION MAINTANENCE", new[] { "No damage detected." });
                        return false;
                    }

                    double cost = Math.Round(Math.Max(0.0, Main.Settings.maxRepairCostFull) * repaired01, 2);

                    string jName = null;
                    try { jName = j.junctionData.junctionIdLong; } catch { }
                    if (string.IsNullOrWhiteSpace(jName))
                        jName = j?.gameObject?.name ?? "Switch";

                    // LicensePayScreen holen
                    var licensePay = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                    if (licensePay == null)
                    {
                        Main.Log("CareerManagerLicensePayingScreen not found.", true);
                        return false;
                    }

                    if (licensePay.cashReg == null)
                    {
                        Main.Log("LicensePayScreen.cashReg is null.", true);
                        return false;
                    }

                    // Transaktion setzen
                    licensePay.cashReg.ClearCurrentTransaction();
                    licensePay.cashReg.SetTotalCost(cost);

                    // Status merken
                    JM_Payment.Start(key, repaired01, cost, jName, before);

                    // Screen aktivieren
                    licensePay.screenSwitcher?.SetActiveDisplay(licensePay);

                    return false;
                }

                case InputAction.Cancel:
                {
                    // Wenn wir aus dem "No damage detected" Prompt kommen: immer zurück zur Liste
                    if (JM_CM_ListState.NoDamagePromptActive)
                    {
                        JM_CM_ListState.NoDamagePromptActive = false;
                        JM_CM_ListState.MoveSelection(-1);
                        return false; // behandelt
                    }

                    // Cancel in der Liste: zurück zum MainScreen
                    if (JM_CM_ListState.MainScreen != null)
                    {
                        __instance.screenSwitcher?.SetActiveDisplay(JM_CM_ListState.MainScreen);
                    }
                    return false;
                }

                default:
                    return true;
            }
        }
    }

    // ---------- Payment-Status ----------
    internal static class JM_Payment
    {
        public static bool Active;
        public static string Key;
        public static float Repaired01;     // Anteil 0..1
        public static double Cost;          // Betrag
        public static string JunctionName;  // Anzeige
        public static float DamageBefore01; // 0..1 vor Reparatur

        public static void Start(string key, float repaired01, double cost, string junctionName, float damageBefore01)
        {
            Active = true;
            Key = key;
            Repaired01 = repaired01;
            Cost = cost;
            JunctionName = junctionName;
            DamageBefore01 = Mathf.Clamp01(damageBefore01);
        }

        public static void Clear()
        {
            Active = false;
            Key = null;
            Repaired01 = 0f;
            Cost = 0.0;
            JunctionName = null;
            DamageBefore01 = 0f;
        }
    }

    // ---------- License-PayScreen: Activate überschreiben (ohne Lizenzdaten) ----------
    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "Activate")]
    internal static class JM_LicensePay_Activate
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance, IDisplayScreen _)
        {
            if (!JM_Payment.Active) return true;

            try
            {
                var cashReg = __instance.cashReg;
                if (cashReg == null)
                {
                    Main.Log("LicensePay.Activate: cashReg is null.", true);
                    return true;
                }

                cashReg.ClearCurrentTransaction();
                cashReg.SetTotalCost(JM_Payment.Cost);

                if (__instance.title1 != null) __instance.title1.text = "JUNCTION MAINTANENCE";
                if (__instance.title2 != null) __instance.title2.text = "Junction:";
                if (__instance.licenseNameText != null) __instance.licenseNameText.text = JM_Payment.JunctionName ?? "Unknown";
                if (__instance.licensePriceText != null) __instance.licensePriceText.text = "$ " + JM_Payment.Cost.ToString("N2", CultureInfo.GetCultureInfo("de-DE"));
                if (__instance.insertWallet != null) __instance.insertWallet.text = "Insert wallet to pay";
                if (__instance.depositedText != null) __instance.depositedText.text = "Deposited";
                if (__instance.depositedValue != null) __instance.depositedValue.text = "$ " + cashReg.DepositedCash.ToString("N2", CultureInfo.GetCultureInfo("de-DE"));

                cashReg.CashAdded -= OnCashAddedCompat;
                cashReg.CashAdded += OnCashAddedCompat;
                return false;
            }
            catch (Exception e)
            {
                Main.Log("LicensePay.Activate override error: " + e, true);
                return true;
            }
        }

        internal static void OnCashAddedCompat()
        {
            try
            {
                var screen = UnityEngine.Object.FindObjectOfType<CareerManagerLicensePayingScreen>();
                if (screen?.cashReg == null || screen.depositedValue == null) return;
                screen.depositedValue.text = "$ " + screen.cashReg.DepositedCash.ToString("N2", CultureInfo.GetCultureInfo("de-DE"));
            }
            catch { }
        }
    }

    // ---------- License-PayScreen: Disable säubern ----------
    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "Disable")]
    internal static class JM_LicensePay_Disable
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance)
        {
            if (!JM_Payment.Active) return true;

            try
            {
                var cashReg = __instance.cashReg;
                if (cashReg != null)
                {
                    cashReg.CashAdded -= JM_LicensePay_Activate.OnCashAddedCompat;
                    cashReg.ClearCurrentTransaction();
                }

                void ClearTMP(TMPro.TextMeshPro tmp) { if (tmp != null) tmp.text = string.Empty; }
                ClearTMP(__instance.title1);
                ClearTMP(__instance.title2);
                ClearTMP(__instance.licenseNameText);
                ClearTMP(__instance.licensePriceText);
                ClearTMP(__instance.insertWallet);
                ClearTMP(__instance.depositedText);
                ClearTMP(__instance.depositedValue);

                return false;
            }
            catch (Exception e)
            {
                Main.Log("LicensePay.Disable override error: " + e, true);
                return true;
            }
        }
    }

    // ---------- License-PayScreen: Eingaben ----------
    [HarmonyPatch(typeof(CareerManagerLicensePayingScreen), "HandleInputAction")]
    internal static class JM_LicensePay_HandleInput
    {
        static bool Prefix(CareerManagerLicensePayingScreen __instance, InputAction input)
        {
            if (!JM_Payment.Active) return true;

            var cashReg = __instance.cashReg;

            switch (input)
            {
                case InputAction.Cancel:
                {
                    try
                    {
                        __instance.cashReg?.ClearCurrentTransaction();

                        // Liste reaktivieren und neu rendern
                        JM_CM_ListState.Active = true;
                        if (JM_CM_ListState.StationMode)
                            JM_CM_ListState.Items = JM_CareerManagerHelpers.GetStationSwitches(JM_CM_ListState.StationCode, JM_CM_ListState.Center);
                        else
                            JM_CM_ListState.Items = JM_CareerManagerHelpers.GetAllSortedWithin(JM_CM_ListState.Center, JM_CM_ListState.Radius);

                        JM_CM_ListState.Selected = Mathf.Clamp(JM_CM_ListState.Selected, 0, Math.Max(0, JM_CM_ListState.Items.Count - 1));
                        JM_CM_ListState.Top      = Mathf.Clamp(JM_CM_ListState.Top, 0, Math.Max(0, JM_CM_ListState.Items.Count - JM_CM_ListState.VISIBLE));
                        JM_CM_ListState.Render();

                        var switcher = __instance.screenSwitcher;
                        IDisplayScreen target = (JM_CM_ListState.Info as IDisplayScreen)
                                                ?? (JM_CM_ListState.MainScreen as IDisplayScreen)
                                                ?? __instance; // Fallback
                        switcher?.SetActiveDisplay(target);
                    }
                    catch (Exception e)
                    {
                        Main.Log("LicensePay.Cancel error: " + e, true);
                        // Notfall: wenigstens zurück zum MainScreen
                        JM_CM_ListState.MainScreen?.screenSwitcher?.SetActiveDisplay(JM_CM_ListState.MainScreen);
                    }
                    finally
                    {
                        // Erst JETZT Payment-Status löschen
                        JM_Payment.Clear();
                    }
                    return false;
                }

                case InputAction.Confirm:
                {
                    try
                    {
                        if (cashReg != null && cashReg.Buy())
                        {
                            float before = DamageStore.Get(JM_Payment.Key);
                            float after  = Mathf.Max(0f, before - JM_Payment.Repaired01);
                            DamageStore.Set(JM_Payment.Key, after);

                            // Zurueck zum MainScreen
                            if (JM_CM_ListState.MainScreen != null)
                            {
                                var switcher = JM_CM_ListState.MainScreen.screenSwitcher;
                                if (switcher != null)
                                    switcher.SetActiveDisplay(JM_CM_ListState.MainScreen);
                            }

                            JM_Payment.Clear();
                        }
                    }
                    catch (Exception e)
                    {
                        Main.Log("LicensePay.Confirm error: " + e, true);
                    }
                    return false;
                }

                default:
                    return true;
            }
        }
    }

    // ---------- Robustheit direkt an der Kasse ----------
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "GetTotalCost")]
    internal static class JM_CashReg_GetTotalCost
    {
        static bool Prefix(ref double __result)
        {
            if (JM_Payment.Active)
            {
                __result = Math.Max(0.0, JM_Payment.Cost);
                return false; // Original überspringen
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "TotalUnitsInBasket")]
    internal static class JM_CashReg_TotalUnits
    {
        static bool Prefix(ref float __result)
        {
            if (JM_Payment.Active)
            {
                __result = 1f; // mindestens eine "Einheit", sonst bricht Buy() ab
                return false;
            }
            return true;
        }
    }

    // ---------- Fallback: Wenn Buy() dennoch direkt am CashRegister passiert ----------
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterCareerManager), "Buy")]
    internal static class JM_Payment_OnBuy
    {
        static void Postfix(bool __result)
        {
            if (!__result || !JM_Payment.Active) return;

            float before = DamageStore.Get(JM_Payment.Key);
            float after  = Mathf.Max(0f, before - JM_Payment.Repaired01);
            DamageStore.Set(JM_Payment.Key, after);

            try
            {
                if (JM_CM_ListState.StationMode)
                    JM_CM_ListState.Items = JM_CareerManagerHelpers.GetStationSwitches(JM_CM_ListState.StationCode, JM_CM_ListState.Center);
                else
                    JM_CM_ListState.Items = JM_CareerManagerHelpers.GetAllSortedWithin(JM_CM_ListState.Center, JM_CM_ListState.Radius);

                JM_CM_ListState.Selected = Mathf.Min(JM_CM_ListState.Selected, Math.Max(0, JM_CM_ListState.Items.Count - 1));
                JM_CM_ListState.Top      = Mathf.Min(JM_CM_ListState.Top, Math.Max(0, JM_CM_ListState.Items.Count - JM_CM_ListState.VISIBLE));

                if (JM_CM_ListState.Info != null && JM_CM_ListState.MainScreen != null)
                {
                    string msg = $"Repaired {JM_Payment.JunctionName} by {JM_Payment.Repaired01*100f:0.#}% – Cost $ {JM_Payment.Cost.ToString("N2", CultureInfo.GetCultureInfo("de-DE"))}";
                    // Erfolgsmeldung schlicht als Paragraph zeigen
                    JM_CareerManagerHelpers.SetInfoLines(JM_CM_ListState.Info, JM_CM_ListState.MainScreen, "JUNCTION MAINTANENCE", new[] { msg });
                    JM_CM_ListState.Render();
                    JM_CM_ListState.MainScreen.screenSwitcher.SetActiveDisplay(JM_CM_ListState.Info);
                }
            }
            catch { }

            JM_Payment.Clear();
        }
    }
}
