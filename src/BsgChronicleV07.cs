using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HarmonyLib;

namespace BSGBestiary
{
    public class BsgChronicleModApi : IModApi
    {
        private static bool initialized;

        public void InitMod(Mod _modInstance)
        {
            if (initialized) return;
            initialized = true;

            try
            {
                ChronicleCatalog.Load();
                ModEvents.GameUpdate.RegisterHandler(OnGameUpdate);
                ModEvents.EntityKilled.RegisterHandler(BestiaryTracker.OnKilled);
                ModEvents.PlayerSpawnedInWorld.RegisterHandler(OnPlayerSpawnedInWorld);

                var harmony = new Harmony("bsg.chronicle.rebirth26.v027ui");
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                Log.Out("[BSG Chronicle] v0.27 UI inicializada. Boton Bestiario y Cronica sincronizados con chat.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error inicializando v0.25: " + ex);
            }
        }

        private static void OnGameUpdate(ref ModEvents.SGameUpdateData data)
        {
            ChronicleState.Tick();
            BestiaryRewardBuff.EnsureLocalPlayerReward();
            BestiaryTracker.Tick();
            BestiaryButtonBridge.Tick();
        }

        private static void OnPlayerSpawnedInWorld(ref ModEvents.SPlayerSpawnedInWorldData data)
        {
            try
            {
                if (!data.IsLocalPlayer) return;
                BestiaryRewardBuff.ApplyCurrentRewards(GameManager.Instance.World.GetPrimaryPlayer());
                BestiaryOverlay.Attach();
                ChronicleState.BindLocal(GameManager.Instance.World.GetPrimaryPlayer() as EntityPlayerLocal);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Error aplicando reward al spawn: " + ex);
            }
        }
    }

    public sealed class ChronicleTitleInfo
    {
        public string Text = string.Empty;
        public string CategoryId = string.Empty;
        public string CategoryName = string.Empty;
        public string KillEntity = string.Empty;
        public int Threshold;
        public float RewardDamagePct;
        public float RewardDismemberPct;
        public string RewardBuff = string.Empty;
    }

    public static class ChronicleCatalog
    {
        private static readonly Dictionary<string, ChronicleTitleInfo> ByText =
            new Dictionary<string, ChronicleTitleInfo>(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            try
            {
                ByText.Clear();

                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string path = Path.Combine(dir, "Config", "playertitles.xml");
                if (!File.Exists(path))
                {
                    Log.Out("[BSG Chronicle] No se encontro Config/playertitles.xml para construir tooltips.");
                    return;
                }

                RepairBadTitleTags(path);
                XDocument doc = XDocument.Load(path);
                XElement root = doc.Root;
                if (root == null) return;

                foreach (XElement cat in root.Elements("category"))
                {
                    string categoryId = Attr(cat, "id");
                    string categoryName = Attr(cat, "name");
                    string killEntity = Attr(cat, "kill_entity");

                    foreach (XElement tier in cat.Elements("tier"))
                    {
                        string text = Attr(tier, "text");
                        int threshold;
                        int.TryParse(Attr(tier, "threshold"), out threshold);
                        if (string.IsNullOrEmpty(text)) continue;

                        ChronicleTitleInfo info = new ChronicleTitleInfo();
                        info.Text = text;
                        info.CategoryId = categoryId;
                        info.CategoryName = categoryName;
                        info.KillEntity = killEntity;
                        info.Threshold = threshold;
                        float rewardDamagePct;
                        float.TryParse(Attr(tier, "reward_damage_pct"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rewardDamagePct);
                        info.RewardDamagePct = rewardDamagePct;

                        float rewardDismemberPct;
                        float.TryParse(Attr(tier, "reward_dismember_pct"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rewardDismemberPct);
                        info.RewardDismemberPct = rewardDismemberPct;
                        info.RewardBuff = Attr(tier, "reward_buff");

                        ByText[text] = info;
                    }
                }

                Log.Out("[BSG Chronicle] Catalogo cargado: " + ByText.Count + " titulos.");
                List<BestiaryFamilyDefinition> familias = GetFamilies();
                Log.Out("[BSG Bestiario] DIAG: familias cargadas=" + familias.Count + " (esperado=15).");
                if (familias.Count != 15)
                    Log.Out("[BSG Bestiario] AVISO: cantidad de familias distinta de 15, revisar Config/playertitles.xml.");
                foreach (BestiaryFamilyDefinition familia in familias)
                {
                    if (string.IsNullOrEmpty(familia.MatchClass))
                        Log.Out("[BSG Bestiario] AVISO: categoria sin kill_entity: " + familia.Id);
                }
                AuditRewardBuffs(dir);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error leyendo playertitles.xml: " + ex);
            }
        }


        // Corrige únicamente el patrón roto introducido por las pruebas:
        // <tier ... / reward_damage_pct="100"> => <tier ... reward_damage_pct="100" />
        // Se ejecuta antes de que PlayerTitles inicialice y lea el mismo XML.
        private static void RepairBadTitleTags(string path)
        {
            string raw = File.ReadAllText(path);
            string repaired = Regex.Replace(raw,
                @"(<tier\b[^>\r\n]*?)\s*/\s*((?:(?:reward_damage_pct|reward_dismember_pct|reward_buff)\s*=\s*""[^""]*""\s*)+)>",
                "$1 $2 />",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (string.Equals(raw, repaired, StringComparison.Ordinal)) return;
            // Validar antes de escribir; mantener los thresholds y el resto intactos.
            XDocument.Parse(repaired);
            string backup = path + ".bsg_antes_de_reparar.bak";
            if (!File.Exists(backup)) File.Copy(path, backup);
            File.WriteAllText(path, repaired);
            Log.Out("[BSG Bestiario] XML de titulos reparado automaticamente. Respaldo: " + backup);
        }

        private static string Attr(XElement e, string name)
        {
            XAttribute a = e.Attribute(name);
            return a == null ? string.Empty : a.Value;
        }

        // Valida que cada recompensa referenciada por playertitles.xml exista
        // realmente en el XML de buffs del mod, sin modificar recompensas.
        private static void AuditRewardBuffs(string modDirectory)
        {
            try
            {
                HashSet<string> families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int missingReference = 0;
                int sharedAcrossFamilies = 0;
                foreach (ChronicleTitleInfo info in ByText.Values)
                {
                    if (!string.IsNullOrEmpty(info.CategoryId))
                        families.Add(info.CategoryId);
                    if (info.RewardDamagePct <= 0f && info.RewardDismemberPct <= 0f)
                        continue;
                    if (string.IsNullOrEmpty(info.RewardBuff))
                    {
                        missingReference++;
                        Log.Out("[BSG Bestiario] AUDITORIA: sin reward_buff para " + info.Text);
                        continue;
                    }

                    referenced.Add(info.RewardBuff);
                    string owner;
                    if (owners.TryGetValue(info.RewardBuff, out owner))
                    {
                        if (!string.Equals(owner, info.CategoryId, StringComparison.OrdinalIgnoreCase))
                        {
                            sharedAcrossFamilies++;
                            Log.Out("[BSG Bestiario] AUDITORIA: buff compartido por familias " +
                                    owner + " / " + info.CategoryId + ": " + info.RewardBuff);
                        }
                    }
                    else
                    {
                        owners.Add(info.RewardBuff, info.CategoryId);
                    }
                }

                string buffsPath = Path.Combine(modDirectory, "Config", "buffs.xml");
                if (!File.Exists(buffsPath))
                {
                    Log.Out("[BSG Bestiario] AUDITORIA: Config/buffs.xml ausente; no puedo verificar " +
                            referenced.Count + " definiciones.");
                    return;
                }

                HashSet<string> defined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                XDocument buffDocument = XDocument.Load(buffsPath);
                foreach (XElement buff in buffDocument.Descendants("buff"))
                {
                    string name = Attr(buff, "name");
                    if (!string.IsNullOrEmpty(name))
                        defined.Add(name);
                }

                int missingDefinitions = 0;
                int missingEffect = 0;
                foreach (string buff in referenced)
                {
                    if (!defined.Contains(buff))
                    {
                        missingDefinitions++;
                        Log.Out("[BSG Bestiario] AUDITORIA: buff no definido en Config/buffs.xml: " + buff);
                        continue;
                    }
                    bool hasRewardEffect = false;
                    foreach (XElement candidate in buffDocument.Descendants("buff"))
                    {
                        if (!string.Equals(Attr(candidate, "name"), buff, StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (XElement effect in candidate.Descendants("passive_effect"))
                        {
                            string effectName = Attr(effect, "name");
                            if (string.Equals(effectName, "DamageModifier", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(effectName, "DismemberChance", StringComparison.OrdinalIgnoreCase))
                            {
                                hasRewardEffect = true;
                                break;
                            }
                        }
                    }
                    if (!hasRewardEffect)
                    {
                        missingEffect++;
                        Log.Out("[BSG Bestiario] AUDITORIA: sin efecto DamageModifier/DismemberChance visible: " + buff);
                    }
                }

                Log.Out("[BSG Bestiario] AUDITORIA v0.19: familias=" + families.Count +
                        " | buffs referenciados=" + referenced.Count +
                        " | buffs definidos=" + defined.Count +
                        " | referencias faltantes=" + missingReference +
                        " | definiciones faltantes=" + missingDefinitions +
                        " | sin efectos visibles=" + missingEffect +
                        " | buffs entre familias=" + sharedAcrossFamilies +
                        ". Revisar en juego el filtro de daño por familia.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] AUDITORIA: error leyendo configuracion de recompensas: " + ex);
            }
        }


        public static ChronicleTitleInfo Find(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;

            ChronicleTitleInfo info;
            string search = title.Trim();
            if (ByText.TryGetValue(search, out info)) return info;

            // Aceptamos el titulo dentro de un mensaje de PlayerTitles, pero nunca
            // interpretamos un titulo incompleto como otro titulo mas largo.
            ChronicleTitleInfo best = null;
            int longest = 0;
            foreach (KeyValuePair<string, ChronicleTitleInfo> pair in ByText)
            {
                if (search.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    pair.Key.Length > longest)
                {
                    best = pair.Value;
                    longest = pair.Key.Length;
                }
            }
            return best;
        }

        public static string BuildTooltip(string title)
        {
            ChronicleTitleInfo info = Find(title);
            if (info == null)
                return title + "\nRequisito: titulo desbloqueado";

            string requirement;
            if (string.Equals(info.KillEntity, "zombie", StringComparison.OrdinalIgnoreCase))
            {
                requirement = "Eliminar " + info.Threshold + " infectados";
            }
            else if (!string.IsNullOrEmpty(info.CategoryName))
            {
                requirement = "Eliminar " + info.Threshold + " — " + info.CategoryName;
            }
            else
            {
                requirement = "Alcanzar " + info.Threshold + " bajas";
            }

            string reward;
            if (info.RewardDamagePct > 0f || info.RewardDismemberPct > 0f)
            {
                List<string> parts = new List<string>();
                string targetName = string.IsNullOrEmpty(info.CategoryName)
                    ? "objetivos"
                    : info.CategoryName.ToLowerInvariant();

                if (info.RewardDamagePct > 0f)
                    parts.Add("+" + info.RewardDamagePct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                              "% daño contra " + targetName);
                if (info.RewardDismemberPct > 0f)
                    parts.Add("+" + info.RewardDismemberPct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                              "% desmembramiento contra " + targetName);
                reward = string.Join(" / ", parts.ToArray());
            }
            else
            {
                reward = "título " + StripBrackets(info.Text);
            }

            return info.Text +
                   "\nRequisito: " + requirement +
                   "\nRecompensa: " + reward;
        }

        private static string StripBrackets(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Trim().TrimStart('[').TrimEnd(']');
        }

        public static IEnumerable<string> KnownTitles()
        {
            return ByText.Keys;
        }

        // Un catálogo compartido alimenta la auditoría, el registro de bajas y el visor.
        public static List<BestiaryFamilyDefinition> GetFamilies()
        {
            Dictionary<string, BestiaryFamilyDefinition> byId =
                new Dictionary<string, BestiaryFamilyDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (ChronicleTitleInfo tier in ByText.Values)
            {
                string id = string.IsNullOrEmpty(tier.CategoryId) ? tier.KillEntity : tier.CategoryId;
                if (string.IsNullOrEmpty(id)) continue;
                // La categoria temporal de pruebas desbloquea titulos, pero no es una familia real.
                if (string.Equals(id, "bsg_test", StringComparison.OrdinalIgnoreCase)) continue;
                BestiaryFamilyDefinition family;
                if (!byId.TryGetValue(id, out family))
                {
                    family = new BestiaryFamilyDefinition {
                        Id = id,
                        Name = string.IsNullOrEmpty(tier.CategoryName) ? id : tier.CategoryName,
                        MatchClass = tier.KillEntity
                    };
                    byId[id] = family;
                }
                family.Tiers.Add(tier);
            }
            List<BestiaryFamilyDefinition> result = new List<BestiaryFamilyDefinition>(byId.Values);
            result.Sort(delegate(BestiaryFamilyDefinition a, BestiaryFamilyDefinition b) {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (BestiaryFamilyDefinition family in result)
                family.Tiers.Sort(delegate(ChronicleTitleInfo a, ChronicleTitleInfo b) {
                    return a.Threshold.CompareTo(b.Threshold);
                });
            return result;
        }

        public static IEnumerable<string> KnownRewardBuffs()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChronicleTitleInfo info in ByText.Values)
            {
                if (info == null || string.IsNullOrEmpty(info.RewardBuff)) continue;
                if (seen.Add(info.RewardBuff))
                    yield return info.RewardBuff;
            }
        }
    }

    public sealed class ChronicleEntry
    {
        public string Line = string.Empty;
        public string Tooltip = string.Empty;
        public DateTime CreatedUtc;
    }

    public static class ChronicleState
    {
        private const int MaxHistory = 4;
        private const double TickerHoldSeconds = 10.0;
        private const double TickerFadeSeconds = 2.0;

        private static readonly List<ChronicleEntry> Entries = new List<ChronicleEntry>();
        private static EntityPlayerLocal LocalPlayer;
        private static DateTime NextUiTickUtc = DateTime.MinValue;

        public static void BindLocal(EntityPlayerLocal player)
        {
            if (player != null) LocalPlayer = player;
        }

        public static void Publish(EntityPlayerLocal localPlayer, string playerName, string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            if (string.IsNullOrEmpty(playerName)) playerName = "Jugador";

            if (localPlayer != null)
                LocalPlayer = localPlayer;

            ChronicleEntry entry = new ChronicleEntry();
            entry.Line = playerName + " consiguió " + title;
            entry.Tooltip = ChronicleCatalog.BuildTooltip(title);
            entry.CreatedUtc = DateTime.UtcNow;

            Entries.Insert(0, entry);
            while (Entries.Count > MaxHistory)
                Entries.RemoveAt(Entries.Count - 1);

            Log.Out("[BSG Chronicle] CRONICA => " + entry.Line);
            Log.Out("[BSG Chronicle] Tooltip => " + entry.Tooltip.Replace("\n", " | "));

            ApplyUi(true);
        }

        public static void Tick()
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                if (now < NextUiTickUtc) return;
                NextUiTickUtc = now.AddMilliseconds(200);

                if (LocalPlayer == null && GameManager.Instance != null &&
                    GameManager.Instance.World != null)
                    LocalPlayer = GameManager.Instance.World.GetPrimaryPlayer() as EntityPlayerLocal;
                if (LocalPlayer == null) return;
                ApplyUi(false);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error actualizando estado visual: " + ex);
            }
        }

        private static void ApplyUi(bool forceTicker)
        {
            try
            {
                if (LocalPlayer == null)
                {
                    Log.Error("[BSG Chronicle] No hay EntityPlayerLocal para escribir la Cronica.");
                    return;
                }

                if (LocalPlayer.PlayerUI == null || LocalPlayer.PlayerUI.xui == null)
                {
                    Log.Error("[BSG Chronicle] PlayerUI/XUi todavía no está disponible.");
                    return;
                }

                XUi xui = LocalPlayer.PlayerUI.xui;
                XUiV_Window chatWindow = xui.GetWindow("chat");
                // El estado IsVisible puede no reflejar el cierre real del chat.
                bool chatOpen = ChatUiState.IsOpen;

                XUiV_Window tickerWindow = xui.GetWindow("bsgChronicleTicker");
                XUiV_Window historyWindow = xui.GetWindow("bsgChronicleHistory");
                XUiController tickerRoot = xui.GetChildById("bsgChronicleTickerRoot");
                XUiController historyRoot = xui.GetChildById("bsgChronicleHistoryRoot");

                if (chatOpen)
                {
                    if (tickerRoot != null && tickerRoot.ViewComponent != null)
                        tickerRoot.ViewComponent.IsVisible = false;

                    if (historyRoot != null && historyRoot.ViewComponent != null)
                        historyRoot.ViewComponent.IsVisible = true;

                    if (historyWindow != null)
                        historyWindow.ForceVisible(1f);

                    FillHistory(xui);
                    return;
                }

                if (historyWindow != null)
                    historyWindow.ForceVisible(0f);
                if (historyRoot != null && historyRoot.ViewComponent != null)
                    historyRoot.ViewComponent.IsVisible = false;

                if (Entries.Count == 0)
                {
                    if (tickerWindow != null) tickerWindow.ForceVisible(0f);
                    if (tickerRoot != null && tickerRoot.ViewComponent != null)
                        tickerRoot.ViewComponent.IsVisible = false;
                    return;
                }

                ChronicleEntry latest = Entries[0];
                double age = (DateTime.UtcNow - latest.CreatedUtc).TotalSeconds;

                if (tickerWindow == null)
                {
                    Log.Error("[BSG Chronicle] No encontré la ventana bsgChronicleTicker.");
                    return;
                }

                if (forceTicker || age < TickerHoldSeconds)
                {
                    if (tickerRoot != null && tickerRoot.ViewComponent != null)
                        tickerRoot.ViewComponent.IsVisible = true;
                    tickerWindow.ForceVisible(1f);
                    FillTicker(xui, latest);
                }
                else if (age < TickerHoldSeconds + TickerFadeSeconds)
                {
                    if (tickerRoot != null && tickerRoot.ViewComponent != null)
                        tickerRoot.ViewComponent.IsVisible = true;
                    float alpha = (float)(1.0 - ((age - TickerHoldSeconds) / TickerFadeSeconds));
                    if (alpha < 0f) alpha = 0f;
                    tickerWindow.ForceVisible(alpha);
                }
                else
                {
                    tickerWindow.ForceVisible(0f);
                    if (tickerRoot != null && tickerRoot.ViewComponent != null)
                        tickerRoot.ViewComponent.IsVisible = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error escribiendo Cronica/Chat UI: " + ex);
            }
        }

        private static void FillTicker(XUi xui, ChronicleEntry entry)
        {
            XUiController ctrl = xui.GetChildById("bsgChronicleTickerLine");
            XUiV_Label label = ctrl == null ? null : ctrl.ViewComponent as XUiV_Label;

            if (label == null)
            {
                Log.Error("[BSG Chronicle] No encontré bsgChronicleTickerLine.");
                return;
            }

            label.Text = entry.Line;
            label.SetTextImmediately(entry.Line);
            label.ToolTip = entry.Tooltip;

            if (ctrl.ViewComponent != null)
                ctrl.ViewComponent.ToolTip = entry.Tooltip;
        }

        private static void FillHistory(XUi xui)
        {
            for (int i = 0; i < MaxHistory; i++)
            {
                string rowId = "bsgChronicleHistoryRow" + i;
                string lineId = "bsgChronicleHistoryLine" + i;

                XUiController row = xui.GetChildById(rowId);
                XUiController ctrl = xui.GetChildById(lineId);
                XUiV_Label label = ctrl == null ? null : ctrl.ViewComponent as XUiV_Label;

                bool hasEntry = i < Entries.Count;
                if (row != null && row.ViewComponent != null)
                    row.ViewComponent.IsVisible = hasEntry;

                if (label == null) continue;

                if (hasEntry)
                {
                    ChronicleEntry entry = Entries[i];
                    label.Text = entry.Line;
                    label.SetTextImmediately(entry.Line);
                    label.ToolTip = entry.Tooltip;

                    if (row != null && row.ViewComponent != null)
                        row.ViewComponent.ToolTip = entry.Tooltip;
                }
                else
                {
                    label.Text = string.Empty;
                    label.SetTextImmediately(string.Empty);
                    label.ToolTip = string.Empty;
                }
            }
        }
    }


    public static class MasteryState
    {
        private static readonly Dictionary<string, HashSet<string>> Unlocked =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static string LoadedPath = string.Empty;
        private static bool RecoveredFromBackup;

        private static string GetPath()
        {
            string dir = GameIO.GetSaveGameDir();
            if (string.IsNullOrEmpty(dir)) return string.Empty;
            return Path.Combine(dir, "bsg_bestiary_state.xml");
        }

        private static void EnsureLoaded()
        {
            string path = GetPath();
            if (string.IsNullOrEmpty(path) || string.Equals(path, LoadedPath, StringComparison.OrdinalIgnoreCase))
                return;

            Unlocked.Clear();
            LoadedPath = path;
            RecoveredFromBackup = false;

            if (!File.Exists(path)) return;

            try
            {
                ReadState(path);
                Log.Out("[BSG Bestiario] Estado persistente cargado: " + path);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Archivo principal invalido: " + ex);
                Unlocked.Clear();
                string backup = path + ".bak";
                if (!File.Exists(backup)) return;
                try
                {
                    ReadState(backup);
                    RecoveredFromBackup = true;
                    Log.Out("[BSG Bestiario] Estado recuperado de respaldo: " + backup);
                }
                catch (Exception backupEx)
                {
                    Unlocked.Clear();
                    Log.Error("[BSG Bestiario] Respaldo tambien invalido: " + backupEx);
                }
            }
        }

        private static void ReadState(string path)
        {
            XDocument doc = XDocument.Load(path);
            XElement root = doc.Root;
            if (root == null || root.Name.LocalName != "bsgBestiary")
                throw new InvalidDataException("La raiz de estado no es bsgBestiary");

            Dictionary<string, HashSet<string>> fromDisk =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement p in root.Elements("player"))
            {
                string name = (string)p.Attribute("name") ?? string.Empty;
                if (string.IsNullOrEmpty(name)) continue;

                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement t in p.Elements("title"))
                {
                    string id = (string)t.Attribute("id") ?? string.Empty;
                    if (!string.IsNullOrEmpty(id)) set.Add(id);
                }
                fromDisk[name] = set;
            }
            Unlocked.Clear();
            foreach (KeyValuePair<string, HashSet<string>> entry in fromDisk)
                Unlocked[entry.Key] = entry.Value;
        }

        public static void Unlock(string playerName, string title)
        {
            if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(title)) return;
            EnsureLoaded();

            HashSet<string> set;
            if (!Unlocked.TryGetValue(playerName, out set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Unlocked[playerName] = set;
            }

            if (!set.Add(title)) return;
            Save();
            Log.Out("[BSG Bestiario] Maestría persistida: " + playerName + " => " + title);
        }

        public static float GetZombieDamageBonus(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return 0f;
            EnsureLoaded();

            HashSet<string> set;
            if (!Unlocked.TryGetValue(playerName, out set)) return 0f;

            float totalPct = 0f;
            foreach (string title in set)
            {
                ChronicleTitleInfo info = ChronicleCatalog.Find(title);
                if (info == null || info.RewardDamagePct <= 0f) continue;
                if (string.Equals(info.KillEntity, "zombie", StringComparison.OrdinalIgnoreCase))
                    totalPct += info.RewardDamagePct;
            }
            return totalPct;
        }

        public static List<ChronicleTitleInfo> GetBestRewardInfos(string playerName)
        {
            List<ChronicleTitleInfo> result = new List<ChronicleTitleInfo>();
            if (string.IsNullOrEmpty(playerName)) return result;
            EnsureLoaded();

            HashSet<string> set;
            if (!Unlocked.TryGetValue(playerName, out set)) return result;

            Dictionary<string, ChronicleTitleInfo> bestByCategory =
                new Dictionary<string, ChronicleTitleInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (string title in set)
            {
                ChronicleTitleInfo info = ChronicleCatalog.Find(title);
                if (info == null || string.IsNullOrEmpty(info.RewardBuff)) continue;

                string key = string.IsNullOrEmpty(info.CategoryId) ? info.KillEntity : info.CategoryId;
                if (string.IsNullOrEmpty(key)) key = info.Text;

                ChronicleTitleInfo current;
                if (!bestByCategory.TryGetValue(key, out current) || info.Threshold > current.Threshold)
                    bestByCategory[key] = info;
            }

            foreach (ChronicleTitleInfo info in bestByCategory.Values)
                result.Add(info);

            return result;
        }

        public static bool HasTitle(string playerName, string title)
        {
            if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(title)) return false;
            EnsureLoaded();

            HashSet<string> set;
            if (!Unlocked.TryGetValue(playerName, out set)) return false;
            return set.Contains(title);
        }

        private static void Save()
        {
            string path = GetPath();
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                XElement root = new XElement("bsgBestiary");
                foreach (KeyValuePair<string, HashSet<string>> pair in Unlocked)
                {
                    XElement p = new XElement("player", new XAttribute("name", pair.Key));
                    foreach (string title in pair.Value)
                        p.Add(new XElement("title", new XAttribute("id", title)));
                    root.Add(p);
                }
                string tempPath = path + ".tmp";
                string backupPath = path + ".bak";
                new XDocument(root).Save(tempPath);
                if (RecoveredFromBackup && File.Exists(backupPath))
                {
                    // Conservamos el original danado antes de restaurar la copia sana.
                    if (File.Exists(path))
                        File.Copy(path, path + ".corrupt", true);
                    File.Copy(backupPath, path, true);
                    RecoveredFromBackup = false;
                }
                if (File.Exists(path))
                    File.Replace(tempPath, path, backupPath);
                else
                    File.Move(tempPath, path);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] No pude guardar estado de forma segura: " + ex);
            }
        }
    }

    public static class BestiaryRewardBuff
    {
        private const string LegacyTestBuff = "buffBSGInfectedMasteryTest100";
        private static DateTime NextCheckUtc = DateTime.MinValue;
        private static string LastSignature = string.Empty;
        private static DateTime NextErrorLogUtc = DateTime.MinValue;
        private static bool warnedMissingBuffMethod;


        public static void ApplyForUnlock(EntityPlayer player, string title)
        {
            ApplyCurrentRewards(player);
        }

        public static void EnsureLocalPlayerReward()
        {
            try
            {
                if (DateTime.UtcNow < NextCheckUtc) return;
                NextCheckUtc = DateTime.UtcNow.AddMilliseconds(250);

                if (GameManager.Instance == null || GameManager.Instance.World == null) return;
                ApplyCurrentRewards(GameManager.Instance.World.GetPrimaryPlayer());
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Error restaurando rewards: " + ex);
            }
        }

        public static void ApplyCurrentRewards(EntityPlayer player)
        {
            try
            {
                if (player == null || player.Buffs == null) return;

                if (player.Buffs.HasBuff(LegacyTestBuff))
                    BuffMethodCompatibility.Invoke(player.Buffs, "RemoveBuff", LegacyTestBuff);

                List<ChronicleTitleInfo> selected = MasteryState.GetBestRewardInfos(player.EntityName);
                HashSet<string> selectedBuffs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < selected.Count; i++)
                {
                    ChronicleTitleInfo info = selected[i];
                    if (info == null || string.IsNullOrEmpty(info.RewardBuff)) continue;
                    selectedBuffs.Add(info.RewardBuff);
                }

                foreach (string buff in ChronicleCatalog.KnownRewardBuffs())
                {
                    if (selectedBuffs.Contains(buff)) continue;
                    if (player.Buffs.HasBuff(buff))
                        BuffMethodCompatibility.Invoke(player.Buffs, "RemoveBuff", buff);
                }

                for (int i = 0; i < selected.Count; i++)
                {
                    ChronicleTitleInfo info = selected[i];
                    if (info == null || string.IsNullOrEmpty(info.RewardBuff)) continue;
                    if (!player.Buffs.HasBuff(info.RewardBuff))
                        BuffMethodCompatibility.Invoke(player.Buffs, "AddBuff", info.RewardBuff);
                }

                List<string> sigParts = new List<string>();
                for (int i = 0; i < selected.Count; i++)
                {
                    ChronicleTitleInfo info = selected[i];
                    if (info == null || string.IsNullOrEmpty(info.RewardBuff)) continue;
                    sigParts.Add(info.CategoryId + "=" + info.RewardBuff);
                }
                sigParts.Sort(StringComparer.OrdinalIgnoreCase);
                string signature = string.Join("|", sigParts.ToArray());

                if (!string.Equals(signature, LastSignature, StringComparison.Ordinal))
                {
                    if (selected.Count == 0)
                    {
                        Log.Out("[BSG Bestiario] Sin recompensas de maestría activas para " + player.EntityName + ".");
                    }
                    else
                    {
                        for (int i = 0; i < selected.Count; i++)
                        {
                            ChronicleTitleInfo info = selected[i];
                            if (info == null || string.IsNullOrEmpty(info.RewardBuff)) continue;
                            Log.Out("[BSG Bestiario] MAESTRIA ACTIVA: " + player.EntityName +
                                    " | categoria=" + info.CategoryId +
                                    " | buff=" + info.RewardBuff +
                                    " | daño=" + info.RewardDamagePct.ToString("0.#") + "%" +
                                    " | desmembramiento=" + info.RewardDismemberPct.ToString("0.#") + "%");
                        }
                    }
                    LastSignature = signature;
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= NextErrorLogUtc)
                {
                    NextErrorLogUtc = DateTime.UtcNow.AddSeconds(30);
                    Log.Error("[BSG Bestiario] Error aplicando rewards actuales: " + ex);
                }
            }
        }
    }


    // Rebirth 2.6 puede cambiar la firma de AddBuff / RemoveBuff con respecto
    // al runtime de referencia usado por GitHub Actions. Resolverla al ejecutar.
    public static class BuffMethodCompatibility
    {
        private static readonly Dictionary<string, MethodInfo> Cache =
            new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Warnings =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static bool Invoke(object buffs, string methodName, string buffName)
        {
            if (buffs == null || string.IsNullOrEmpty(buffName)) return false;
            try
            {
                Type type = buffs.GetType();
                string key = type.FullName + "." + methodName;
                MethodInfo method;
                if (!Cache.TryGetValue(key, out method))
                {
                    ParameterInfo[] best = null;
                    MethodInfo selected = null;
                    foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)) continue;
                        ParameterInfo[] ps = candidate.GetParameters();
                        if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                        if (selected == null || ps.Length < best.Length)
                        {
                            selected = candidate;
                            best = ps;
                        }
                    }
                    method = selected;
                    Cache[key] = method;
                }

                if (method == null)
                {
                    if (Warnings.Add(key))
                        Log.Out("[BSG Bestiario] Buff: no existe firma compatible para " + key);
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                args[0] = buffName;
                for (int i = 1; i < args.Length; i++)
                {
                    ParameterInfo p = parameters[i];
                    if (p.HasDefaultValue && p.DefaultValue != DBNull.Value)
                        args[i] = p.DefaultValue;
                    else if (p.ParameterType.IsValueType)
                        args[i] = Activator.CreateInstance(p.ParameterType);
                    else
                        args[i] = null;
                }
                method.Invoke(buffs, args);
                return true;
            }
            catch (Exception ex)
            {
                string warning = methodName + ":" + buffName + ":" + ex.GetType().Name;
                if (Warnings.Add(warning))
                    Log.Error("[BSG Bestiario] Fallo de firma de buff (una vez): " + warning + " " + ex.Message);
                return false;
            }
        }
    }

    public static class ChatUiState
    {
        public static bool IsOpen;
        public static void Changed(bool open)
        {
            IsOpen = open;
            Log.Out("[BSG Chronicle] Chat " + (open ? "abierto" : "cerrado"));
        }
    }

    [HarmonyPatch]
    public static class ChatUiOpenClosePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type t = AccessTools.TypeByName("XUiC_Chat");
            if (t == null)
            {
                Log.Out("[BSG Chronicle] No se encontro XUiC_Chat.");
                yield break;
            }
            foreach (string name in new string[] { "OnOpen", "OnClose" })
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, name);
                if (m == null) m = AccessTools.Method(t, name);
                if (m != null)
                {
                    Log.Out("[BSG Chronicle] Hook chat: " + m.DeclaringType.FullName + "." + name);
                    yield return m;
                }
                else Log.Out("[BSG Chronicle] Metodo chat faltante: " + name);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(MethodBase __originalMethod)
        {
            if (__originalMethod != null)
                ChatUiState.Changed(__originalMethod.Name == "OnOpen");
        }
    }

    // Suscribirse al boton desde el controlador de personaje y no solo
    // desde el arbol global de XUi: los controles de ventanas no siempre
    // aparecen en xui.GetChildById().
    public static class BestiaryButtonBridge
    {
        private static XUiController bound;
        private static DateTime nextCheckUtc = DateTime.MinValue;
        private static DateTime nextLogUtc = DateTime.MinValue;

        public static void Tick()
        {
            try
            {
                if (DateTime.UtcNow < nextCheckUtc) return;
                nextCheckUtc = DateTime.UtcNow.AddMilliseconds(750);
                if (GameManager.Instance == null || GameManager.Instance.World == null) return;
                EntityPlayerLocal local = GameManager.Instance.World.GetPrimaryPlayer() as EntityPlayerLocal;
                if (local == null || local.PlayerUI == null || local.PlayerUI.xui == null) return;
                XUi xui = local.PlayerUI.xui;
                XUiController frame = xui.GetChildById("CharacterFrameWindow");
                if (frame != null) Bind(frame);
                if (bound == null)
                {
                    XUiController btn = xui.GetChildById("bsgBestiaryButton");
                    if (btn != null) BindButton(btn);
                }
                if (bound == null && DateTime.UtcNow >= nextLogUtc)
                {
                    nextLogUtc = DateTime.UtcNow.AddSeconds(30);
                    Log.Out("[BSG Bestiario] DIAG: boton B no encontrado aun.");
                }
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow >= nextLogUtc)
                {
                    nextLogUtc = DateTime.UtcNow.AddSeconds(30);
                    Log.Error("[BSG Bestiario] DIAG: error conectando boton B: " + ex);
                }
            }
        }

        public static void Bind(XUiController frame)
        {
            if (frame == null) return;
            XUiController button = frame.GetChildById("bsgBestiaryButton");
            if (button != null) BindButton(button);
        }

        private static void BindButton(XUiController button)
        {
            if (object.ReferenceEquals(bound, button)) return;
            if (bound != null) bound.OnPress -= HandlePress;
            bound = button;
            bound.OnPress += HandlePress;
            Log.Out("[BSG Bestiario] Boton B conectado: " + bound.GetType().FullName);
        }

        private static void HandlePress(XUiController sender, int mouseButton)
        {
            Log.Out("[BSG Bestiario] Click boton B: " + mouseButton);
            if (mouseButton == 0)
                BestiaryOverlay.Toggle();
        }
    }

    [HarmonyPatch]
    public static class CharacterFrameBestiaryPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type t = AccessTools.TypeByName("XUiC_CharacterFrameWindow");
            if (t == null)
            {
                Log.Out("[BSG Bestiario] No se encontro XUiC_CharacterFrameWindow.");
                yield break;
            }
            foreach (string name in new string[] { "Init", "OnOpen" })
            {
                MethodInfo method = AccessTools.DeclaredMethod(t, name);
                if (method != null)
                {
                    Log.Out("[BSG Bestiario] Hook de personaje: " + name);
                    yield return method;
                }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try { BestiaryButtonBridge.Bind(__instance as XUiController); }
            catch (Exception ex)
            {
                Log.Out("[BSG Bestiario] DIAG: intento de conexion diferido: " + ex.Message);
            }
        }
    }

    public static class PlayerTitlesBridge
    {
        public static void OnNotify(MethodBase original, object instance, object[] args)
        {
            try
            {
                string title = ExtractTitle(args);
                if (string.IsNullOrEmpty(title))
                {
                    Log.Out("[BSG Chronicle] PlayerTitles.NotifyPlayer detectado pero no pude extraer el titulo. " +
                            DescribeArgs(original, args));
                    return;
                }

                EntityPlayerLocal localPlayer = ExtractLocalPlayer(args);

                string playerName = ExtractPlayerName(args);
                if (string.IsNullOrEmpty(playerName) && localPlayer != null)
                    playerName = localPlayer.EntityName;
                if (string.IsNullOrEmpty(playerName))
                    playerName = ResolveLocalPlayerName();

                Log.Out("[BSG Chronicle] PlayerTitles desbloqueo detectado: jugador='" +
                        playerName + "' titulo='" + title + "'. " + DescribeArgs(original, args));

                MasteryState.Unlock(playerName, title);
                BestiaryRewardBuff.ApplyForUnlock(localPlayer, title);
                ChronicleState.Publish(localPlayer, playerName, title);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error procesando NotifyPlayer de PlayerTitles: " + ex);
            }
        }

        private static string ExtractTitle(object[] args)
        {
            if (args == null) return string.Empty;

            // 1) Mensajes/string directos.
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i] as string;
                string title = FindKnownTitleInText(s);
                if (!string.IsNullOrEmpty(title)) return title;
            }

            // 2) Objetos TitleTier u objetos que contengan campos Text/title.
            for (int i = 0; i < args.Length; i++)
            {
                string title = ExtractTitleFromObject(args[i], 0);
                if (!string.IsNullOrEmpty(title)) return title;
            }

            return string.Empty;
        }

        private static string ExtractTitleFromObject(object obj, int depth)
        {
            if (obj == null || depth > 2) return string.Empty;

            string direct = obj as string;
            if (direct != null) return FindKnownTitleInText(direct);

            Type t = obj.GetType();
            if (typeof(Entity).IsAssignableFrom(t)) return string.Empty;

            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo[] fields;
            try { fields = t.GetFields(flags); }
            catch { fields = new FieldInfo[0]; }

            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try { value = fields[i].GetValue(obj); }
                catch { continue; }

                string s = value as string;
                if (s != null)
                {
                    string known = FindKnownTitleInText(s);
                    if (!string.IsNullOrEmpty(known)) return known;

                    if (LooksLikeTitleMember(fields[i].Name) && LooksLikeBracketTitle(s))
                        return s;
                }
            }

            PropertyInfo[] props;
            try { props = t.GetProperties(flags); }
            catch { props = new PropertyInfo[0]; }

            for (int i = 0; i < props.Length; i++)
            {
                if (props[i].GetIndexParameters().Length != 0) continue;

                object value;
                try { value = props[i].GetValue(obj, null); }
                catch { continue; }

                string s = value as string;
                if (s != null)
                {
                    string known = FindKnownTitleInText(s);
                    if (!string.IsNullOrEmpty(known)) return known;

                    if (LooksLikeTitleMember(props[i].Name) && LooksLikeBracketTitle(s))
                        return s;
                }
            }

            return string.Empty;
        }

        private static bool LooksLikeTitleMember(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("title") || n == "text" || n.Contains("tier");
        }

        private static bool LooksLikeBracketTitle(string s)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf('[') >= 0 && s.IndexOf(']') > s.IndexOf('[');
        }

        private static string FindKnownTitleInText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            foreach (string known in ChronicleCatalog.KnownTitles())
                if (s.IndexOf(known, StringComparison.OrdinalIgnoreCase) >= 0)
                    return known;

            int a = s.IndexOf('[');
            int b = a >= 0 ? s.IndexOf(']', a + 1) : -1;
            if (a >= 0 && b > a)
            {
                string bracket = s.Substring(a, b - a + 1);
                if (ChronicleCatalog.Find(bracket) != null)
                    return bracket;
            }

            return string.Empty;
        }

        private static EntityPlayerLocal ExtractLocalPlayer(object[] args)
        {
            if (args == null) return null;

            for (int i = 0; i < args.Length; i++)
            {
                EntityPlayerLocal local = args[i] as EntityPlayerLocal;
                if (local != null) return local;
            }

            return null;
        }

        private static string ExtractPlayerName(object[] args)
        {
            if (args == null) return string.Empty;

            for (int i = 0; i < args.Length; i++)
            {
                EntityPlayer p = args[i] as EntityPlayer;
                if (p != null && !string.IsNullOrEmpty(p.EntityName))
                    return p.EntityName;

                string nested = ExtractPlayerNameFromObject(args[i]);
                if (!string.IsNullOrEmpty(nested))
                    return nested;
            }

            return string.Empty;
        }

        private static string ExtractPlayerNameFromObject(object obj)
        {
            if (obj == null) return string.Empty;
            Type t = obj.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo[] fields;
            try { fields = t.GetFields(flags); }
            catch { fields = new FieldInfo[0]; }

            for (int i = 0; i < fields.Length; i++)
            {
                object value;
                try { value = fields[i].GetValue(obj); }
                catch { continue; }

                EntityPlayer p = value as EntityPlayer;
                if (p != null && !string.IsNullOrEmpty(p.EntityName))
                    return p.EntityName;
            }

            PropertyInfo[] props;
            try { props = t.GetProperties(flags); }
            catch { props = new PropertyInfo[0]; }

            for (int i = 0; i < props.Length; i++)
            {
                if (props[i].GetIndexParameters().Length != 0) continue;

                object value;
                try { value = props[i].GetValue(obj, null); }
                catch { continue; }

                EntityPlayer p = value as EntityPlayer;
                if (p != null && !string.IsNullOrEmpty(p.EntityName))
                    return p.EntityName;
            }

            return string.Empty;
        }

        private static string ResolveLocalPlayerName()
        {
            try
            {
                object world = GameManager.Instance == null ? null : GameManager.Instance.World;
                if (world == null) return string.Empty;

                string[] methodNames = new string[] { "GetPrimaryPlayer", "GetLocalPlayer", "GetLocalPlayers" };
                for (int i = 0; i < methodNames.Length; i++)
                {
                    MethodInfo m = world.GetType().GetMethod(
                        methodNames[i],
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        Type.EmptyTypes,
                        null);

                    if (m == null) continue;

                    object result = m.Invoke(world, null);
                    EntityPlayer p = result as EntityPlayer;
                    if (p != null && !string.IsNullOrEmpty(p.EntityName))
                        return p.EntityName;

                    IEnumerable enumerable = result as IEnumerable;
                    if (enumerable != null)
                    {
                        foreach (object item in enumerable)
                        {
                            EntityPlayer ep = item as EntityPlayer;
                            if (ep != null && !string.IsNullOrEmpty(ep.EntityName))
                                return ep.EntityName;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Out("[BSG Chronicle] No pude resolver jugador local: " + ex.Message);
            }

            return string.Empty;
        }

        private static string DescribeArgs(MethodBase method, object[] args)
        {
            try
            {
                string s = method == null ? "metodo=?" : "metodo=" + method.DeclaringType.FullName + "." + method.Name;
                if (args == null) return s + " args=null";

                for (int i = 0; i < args.Length; i++)
                {
                    object a = args[i];
                    if (a == null)
                        s += " | arg" + i + "=null";
                    else
                        s += " | arg" + i + "=" + a.GetType().FullName + ":" + SafeToString(a);
                }
                return s;
            }
            catch
            {
                return "args no descriptibles";
            }
        }

        private static string SafeToString(object value)
        {
            try
            {
                string s = value.ToString();
                if (s == null) return string.Empty;
                if (s.Length > 180) s = s.Substring(0, 180);
                return s.Replace("\r", " ").Replace("\n", " ");
            }
            catch
            {
                return "<ToString fallo>";
            }
        }
    }

    // Esta es la fuente exacta que ya sabemos que genera el banner inferior.
    // En vez de intentar adivinar el evento de muerte de Rebirth, copiamos el
    // desbloqueo cuando PlayerTitles decide notificarlo.
    [HarmonyPatch]
    public static class PlayerTitlesNotifyPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            int count = 0;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Assembly asm = assemblies[a];
                string asmName = string.Empty;
                try { asmName = asm.GetName().Name; }
                catch { }

                if (!string.Equals(asmName, "PlayerTitles", StringComparison.OrdinalIgnoreCase))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }

                if (types == null) continue;

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type == null) continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                    catch
                    {
                        continue;
                    }

                    for (int m = 0; m < methods.Length; m++)
                    {
                        MethodInfo method = methods[m];
                        if (method.Name != "NotifyPlayer") continue;

                        count++;
                        Log.Out("[BSG Chronicle] Hook PlayerTitles => " + type.FullName + "." + method.Name);
                        yield return method;
                    }
                }
            }

            if (count == 0)
                Log.Error("[BSG Chronicle] No encontre PlayerTitles.NotifyPlayer para parchear.");
        }

        [HarmonyPrefix]
        private static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            // NotifyPlayer es la ruta que PlayerTitles usa para mostrar su banner inferior.
            // El desbloqueo/estado ya fue decidido antes de llegar aquí. Copiamos los datos
            // a nuestra Crónica y evitamos el banner duplicado (incluido el texto francés).
            PlayerTitlesBridge.OnNotify(__originalMethod, __instance, __args);
            Log.Out("[BSG Chronicle] Aviso nativo de PlayerTitles suprimido; se usa CRONICA.");
            return false;
        }
    }

}
