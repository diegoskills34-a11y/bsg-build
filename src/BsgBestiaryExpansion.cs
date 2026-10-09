using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using UnityEngine;

namespace BSGBestiary
{
    // v0.21: contadores adicionales por familia y tipo de criatura.
    // Los titulos y sus requisitos existentes siguen siendo responsabilidad de PlayerTitles.
    // Estos contadores comienzan cuando se instala esta extension; no se simulan bajas pasadas.
    public sealed class BestiaryFamilyCount
    {
        public string Id = "";
        public int Kills;
        public readonly Dictionary<string, BestiarySpeciesCount> Species =
            new Dictionary<string, BestiarySpeciesCount>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BestiarySpeciesCount
    {
        public string ClassName = "";
        public string DisplayName = "";
        public int Kills;
    }

    public static class BestiaryTracker
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Dictionary<string, BestiaryFamilyCount>> Players =
            new Dictionary<string, Dictionary<string, BestiaryFamilyCount>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, DateTime> RecentVictims = new Dictionary<int, DateTime>();
        private static string LoadedPath = "";
        private static bool Dirty;
        private static DateTime NextSaveUtc = DateTime.MinValue;
        private static int LoggedUnclassified;
        private static int LoggedKills;
        private static int SuppressedDuplicateKills;

        private static string StatePath()
        {
            string save = GameIO.GetSaveGameDir();
            return string.IsNullOrEmpty(save) ? "" : Path.Combine(save, "bsg_bestiary_kills.xml");
        }

        private static void EnsureLoaded()
        {
            string path = StatePath();
            if (string.IsNullOrEmpty(path) || string.Equals(path, LoadedPath, StringComparison.OrdinalIgnoreCase))
                return;
            lock (Gate)
            {
                // Nunca abandonar cambios de un mundo anterior sin guardarlos.
                if (Dirty && !string.IsNullOrEmpty(LoadedPath))
                    SaveTo(LoadedPath);
                Players.Clear();
                RecentVictims.Clear();
                Dirty = false;
                LoadedPath = path;
                NextSaveUtc = DateTime.MinValue;
                if (!File.Exists(path)) return;
                try
                {
                    ReadFrom(path);
                    Log.Out("[BSG Bestiario] v0.21: registro de criaturas cargado.");
                }
                catch (Exception ex)
                {
                    Log.Error("[BSG Bestiario] Registro de criaturas dañado: " + ex.Message);
                    Players.Clear();
                    if (!File.Exists(path + ".bak")) return;
                    try
                    {
                        ReadFrom(path + ".bak");
                        Log.Out("[BSG Bestiario] Registro de criaturas recuperado desde .bak");
                    }
                    catch (Exception fallbackEx)
                    {
                        Log.Error("[BSG Bestiario] Respaldo invalido: " + fallbackEx.Message);
                        Players.Clear();
                    }
                }
            }
        }

        private static void ReadFrom(string path)
        {
            XDocument xml = XDocument.Load(path);
            if (xml.Root == null || xml.Root.Name.LocalName != "bsgKills")
                throw new InvalidDataException("Raiz bsgKills no encontrada");
            Dictionary<string, Dictionary<string, BestiaryFamilyCount>> result =
                new Dictionary<string, Dictionary<string, BestiaryFamilyCount>>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement player in xml.Root.Elements("player"))
            {
                string playerName = (string)player.Attribute("name") ?? "";
                if (playerName.Length == 0) continue;
                Dictionary<string, BestiaryFamilyCount> families =
                    new Dictionary<string, BestiaryFamilyCount>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement f in player.Elements("family"))
                {
                    string id = (string)f.Attribute("id") ?? "";
                    if (id.Length == 0) continue;
                    BestiaryFamilyCount count = new BestiaryFamilyCount();
                    count.Id = id;
                    count.Kills = ClampCount((string)f.Attribute("kills"));
                    foreach (XElement item in f.Elements("creature"))
                    {
                        string cls = (string)item.Attribute("class") ?? "";
                        if (cls.Length == 0) continue;
                        count.Species[cls] = new BestiarySpeciesCount {
                            ClassName = cls,
                            DisplayName = (string)item.Attribute("name") ?? cls,
                            Kills = ClampCount((string)item.Attribute("kills"))
                        };
                    }
                    families[id] = count;
                }
                result[playerName] = families;
            }
            Players.Clear();
            foreach (KeyValuePair<string, Dictionary<string, BestiaryFamilyCount>> p in result)
                Players[p.Key] = p.Value;
        }

        private static int ClampCount(string raw)
        {
            int n;
            return int.TryParse(raw, out n) && n > 0 ? n : 0;
        }

        public static void OnKilled(ref ModEvents.SEntityKilledData data)
        {
            try
            {
                EntityPlayer killer = data.KillingEntity as EntityPlayer;
                EntityAlive victim = data.KilledEntitiy as EntityAlive;
                if (killer == null || victim == null || string.IsNullOrEmpty(killer.EntityName)) return;

                EnsureLoaded();
                string entityClass = ResolveClassName(victim);
                if (string.IsNullOrEmpty(entityClass)) return;
                List<BestiaryFamilyDefinition> definitions = BestiaryFamilyDefinition.Read();
                if (definitions.Count == 0) return;

                // Entity.entityId es el identificador de la victima dentro del mundo.
                // GetHashCode() depende de la instancia CLR y no sirve para dedupe.
                int id = victim.entityId;
                if (id <= 0)
                {
                    Log.Out("[BSG Bestiario] Aviso: victima sin entityId valido: " + entityClass);
                    return;
                }
                DateTime now = DateTime.UtcNow;
                lock (Gate)
                {
                    DateTime seen;
                    if (RecentVictims.TryGetValue(id, out seen) && (now - seen).TotalSeconds < 60d)
                    {
                        SuppressedDuplicateKills++;
                        if (SuppressedDuplicateKills <= 5 || SuppressedDuplicateKills % 100 == 0)
                            Log.Out("[BSG Bestiario] DIAG: baja duplicada ignorada entityId=" + id +
                                    " (acumulado=" + SuppressedDuplicateKills + ")");
                        return;
                    }
                    RecentVictims[id] = now;
                    if (RecentVictims.Count > 2000)
                    {
                        List<int> old = new List<int>();
                        foreach (KeyValuePair<int, DateTime> pair in RecentVictims)
                            if ((now - pair.Value).TotalSeconds > 60d) old.Add(pair.Key);
                        foreach (int key in old) RecentVictims.Remove(key);
                        if (RecentVictims.Count > 2000) RecentVictims.Clear();
                    }

                    Dictionary<string, BestiaryFamilyCount> families;
                    if (!Players.TryGetValue(killer.EntityName, out families))
                    {
                        families = new Dictionary<string, BestiaryFamilyCount>(StringComparer.OrdinalIgnoreCase);
                        Players[killer.EntityName] = families;
                    }

                    int matched = 0;
                    foreach (BestiaryFamilyDefinition def in definitions)
                    {
                        if (string.IsNullOrEmpty(def.MatchClass) ||
                            entityClass.IndexOf(def.MatchClass, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        matched++;
                        BestiaryFamilyCount fam;
                        if (!families.TryGetValue(def.Id, out fam))
                        {
                            fam = new BestiaryFamilyCount { Id = def.Id };
                            families[def.Id] = fam;
                        }
                        if (fam.Kills < int.MaxValue) fam.Kills++;
                        BestiarySpeciesCount creature;
                        if (!fam.Species.TryGetValue(entityClass, out creature))
                        {
                            creature = new BestiarySpeciesCount {
                                ClassName = entityClass, DisplayName = ResolveDisplayName(victim, entityClass)
                            };
                            fam.Species[entityClass] = creature;
                        }
                        if (creature.Kills < int.MaxValue) creature.Kills++;
                    }
                    if (matched == 0 && LoggedUnclassified++ < 15)
                        Log.Out("[BSG Bestiario] SIN FAMILIA: " + entityClass +
                                " | entityId=" + id + " | jugador=" + killer.EntityName);
                    if (matched > 0)
                    {
                        LoggedKills++;
                        if (LoggedKills <= 12 || LoggedKills % 100 == 0)
                            Log.Out("[BSG Bestiario] DIAG: muerte aceptada entityId=" + id +
                                    " | clase=" + entityClass +
                                    " | jugador=" + killer.EntityName +
                                    " | familias coincidentes=" + matched +
                                    " | muestra=" + LoggedKills);
                    }
                    if (matched > 0)
                    {
                        Dirty = true;
                        if (NextSaveUtc == DateTime.MinValue) NextSaveUtc = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Error en EntityKilled v0.25: " + ex);
            }
        }

        public static void Tick()
        {
            try
            {
                EnsureLoaded();
                lock (Gate)
                {
                    if (!Dirty || DateTime.UtcNow < NextSaveUtc) return;
                    if (SaveTo(LoadedPath))
                        NextSaveUtc = DateTime.UtcNow.AddSeconds(5);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Error al guardar bajas: " + ex);
            }
        }

        private static bool SaveTo(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                XElement root = new XElement("bsgKills", new XAttribute("schema", "1"));
                foreach (KeyValuePair<string, Dictionary<string, BestiaryFamilyCount>> p in Players)
                {
                    XElement player = new XElement("player", new XAttribute("name", p.Key));
                    foreach (BestiaryFamilyCount family in p.Value.Values)
                    {
                        XElement f = new XElement("family",
                            new XAttribute("id", family.Id),
                            new XAttribute("kills", family.Kills));
                        foreach (BestiarySpeciesCount creature in family.Species.Values)
                            f.Add(new XElement("creature",
                                new XAttribute("class", creature.ClassName),
                                new XAttribute("name", creature.DisplayName),
                                new XAttribute("kills", creature.Kills)));
                        player.Add(f);
                    }
                    root.Add(player);
                }
                string temp = path + ".tmp";
                new XDocument(root).Save(temp);
                if (File.Exists(path))
                    File.Replace(temp, path, path + ".bak");
                else
                    File.Move(temp, path);
                Dirty = false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Persistencia v0.21 fallida: " + ex);
                return false;
            }
        }

        public static BestiaryFamilyCount Snapshot(string playerName, string categoryId)
        {
            EnsureLoaded();
            lock (Gate)
            {
                BestiaryFamilyCount copy = new BestiaryFamilyCount { Id = categoryId };
                Dictionary<string, BestiaryFamilyCount> families;
                BestiaryFamilyCount original;
                if (!Players.TryGetValue(playerName ?? "", out families) ||
                    !families.TryGetValue(categoryId ?? "", out original)) return copy;
                copy.Kills = original.Kills;
                foreach (BestiarySpeciesCount item in original.Species.Values)
                    copy.Species[item.ClassName] = new BestiarySpeciesCount {
                        ClassName = item.ClassName, DisplayName = item.DisplayName, Kills = item.Kills
                    };
                return copy;
            }
        }

        private static object GetMember(Type t, object obj, string name, bool stat)
        {
            if (t == null) return null;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                (stat ? BindingFlags.Static : BindingFlags.Instance);
            FieldInfo field = t.GetField(name, flags);
            if (field != null) return field.GetValue(obj);
            PropertyInfo prop = t.GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0) return prop.GetValue(obj, null);
            return null;
        }

        private static string ResolveClassName(EntityAlive victim)
        {
            try
            {
                Type entityType = victim.GetType();
                object classId = null;
                for (Type t = entityType; t != null && classId == null; t = t.BaseType)
                    classId = GetMember(t, victim, "entityClass", false);
                object all = GetMember(typeof(EntityClass), null, "list", true);
                if (classId != null && all != null)
                {
                    int id = Convert.ToInt32(classId, CultureInfo.InvariantCulture);
                    object entry = null;
                    Array arr = all as Array;
                    if (arr != null && id >= 0 && id < arr.Length) entry = arr.GetValue(id);
                    IDictionary dic = all as IDictionary;
                    if (dic != null && dic.Contains(id)) entry = dic[id];
                    if (entry != null)
                    {
                        object name = GetMember(entry.GetType(), entry, "entityClassName", false);
                        if (name != null && !string.IsNullOrEmpty(name.ToString()))
                            return name.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Out("[BSG Bestiario] Aviso al resolver clase: " + ex.Message);
            }
            return victim.GetType().Name;
        }

        private static string ResolveDisplayName(EntityAlive victim, string fallback)
        {
            try
            {
                string name = victim.EntityName;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return fallback;
        }
    }

    public sealed class BestiaryFamilyDefinition
    {
        public string Id = "";
        public string Name = "";
        public string MatchClass = "";
        public readonly List<ChronicleTitleInfo> Tiers = new List<ChronicleTitleInfo>();

        public static List<BestiaryFamilyDefinition> Read()
        {
            return ChronicleCatalog.GetFamilies();
        }
    }

    // v0.22/0.23: visor independiente de XUi; evita sobreescribir los XML
    // del panel de titulos y de la Cronica ya estabilizados.
    public sealed class BestiaryOverlay : MonoBehaviour
    {
        private static BestiaryOverlay Instance;
        private bool opened;
        private int selected;
        private Vector2 familyScroll;
        private Vector2 creatureScroll;
        private Rect rect = new Rect(85f, 60f, 1010f, 675f);
        private GUIStyle titleStyle;
        private GUIStyle headerStyle;
        private GUIStyle bodyStyle;
        private GUIStyle subduedStyle;
        private GUIStyle selectedButton;
        private GUIStyle tinyStyle;
        private Texture2D panelTexture;
        private bool stylesReady;
        private const int WindowId = 712680;

        public static void Attach()
        {
            if (Instance != null) return;
            try
            {
                GameObject g = new GameObject("BSG_Bestiario_Overlay");
                UnityEngine.Object.DontDestroyOnLoad(g);
                Instance = g.AddComponent<BestiaryOverlay>();
                Log.Out("[BSG Bestiario] UI v0.24 preparada. Se abre con el boton del personaje.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] No pude inicializar UI: " + ex);
            }
        }

        public static void Toggle()
        {
            if (Instance == null) Attach();
            if (Instance == null) return;
            Instance.opened = !Instance.opened;
            Log.Out("[BSG Bestiario] DIAG: BestiaryOverlay.Toggle => " + (Instance.opened ? "ABIERTO" : "CERRADO"));
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;
            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 23; titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.normal.textColor = new Color(0.94f, 0.81f, 0.57f);
            headerStyle = new GUIStyle(GUI.skin.label);
            headerStyle.fontSize = 17; headerStyle.fontStyle = FontStyle.Bold;
            headerStyle.normal.textColor = Color.white;
            bodyStyle = new GUIStyle(GUI.skin.label);
            bodyStyle.fontSize = 14; bodyStyle.wordWrap = true;
            bodyStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            subduedStyle = new GUIStyle(bodyStyle);
            subduedStyle.normal.textColor = new Color(0.7f, 0.75f, 0.79f);
            tinyStyle = new GUIStyle(subduedStyle);
            tinyStyle.fontSize = 12;
            selectedButton = new GUIStyle(GUI.skin.button);
            selectedButton.alignment = TextAnchor.MiddleLeft;
            selectedButton.fontSize = 13;
            panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            panelTexture.SetPixel(0, 0, new Color(0.065f, 0.085f, 0.105f, 0.97f));
            panelTexture.Apply();
            stylesReady = true;
        }

        private void OnGUI()
        {
            if (!opened) return;
            try
            {
                EnsureStyles();
                rect.width = Mathf.Min(1010f, Screen.width - 18f);
                rect.height = Mathf.Min(675f, Screen.height - 18f);
                rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - rect.width));
                rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - rect.height));
                GUI.color = Color.white;
                GUI.DrawTexture(rect, panelTexture);
                rect = GUI.Window(WindowId, rect, Render, "", GUIStyle.none);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] UI no disponible: " + ex);
                opened = false;
            }
        }

        private void Render(int windowId)
        {
            List<BestiaryFamilyDefinition> families = BestiaryFamilyDefinition.Read();
            if (selected < 0) selected = Math.Max(0, families.Count - 1);
            if (selected >= families.Count) selected = 0;
            EntityPlayer local = null;
            try { if (GameManager.Instance != null && GameManager.Instance.World != null)
                    local = GameManager.Instance.World.GetPrimaryPlayer(); }
            catch { }
            string playerName = local == null ? "" : local.EntityName;
            GUILayout.BeginArea(new Rect(15, 10, rect.width - 30, rect.height - 20));
            GUILayout.BeginHorizontal();
            GUILayout.Label("BSG  /  BESTIARIO", titleStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("CERRAR  X", GUILayout.Width(110), GUILayout.Height(32))) opened = false;
            GUILayout.EndHorizontal();
            GUILayout.Label("Bestiario: seleccionar familia con el mouse   |   Estadisticas registradas desde v0.21", subduedStyle);
            GUILayout.Space(9f);

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(Mathf.Min(305f, rect.width * 0.34f)));
            GUILayout.Label("FAMILIAS (" + families.Count + ")", headerStyle);
            familyScroll = GUILayout.BeginScrollView(familyScroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < families.Count; i++)
            {
                BestiaryFamilyDefinition family = families[i];
                BestiaryFamilyCount count = BestiaryTracker.Snapshot(playerName, family.Id);
                GUI.color = i == selected ? new Color(0.99f, 0.81f, 0.54f) : Color.white;
                string label = (i == selected ? ">>  " : "     ") + family.Name + "   [" + count.Kills + "]";
                if (GUILayout.Button(label, selectedButton, GUILayout.Height(30f))) selected = i;
            }
            GUI.color = Color.white;
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            if (families.Count == 0)
            {
                GUILayout.Label("No se encontraron familias. Revisar Config/playertitles.xml.", headerStyle);
            }
            else
            {
                RenderFamily(playerName, families[selected]);
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.DragWindow(new Rect(0, 0, rect.width - 140, 35));
        }

        private void RenderFamily(string playerName, BestiaryFamilyDefinition family)
        {
            BestiaryFamilyCount count = BestiaryTracker.Snapshot(playerName, family.Id);
            GUILayout.Label(family.Name.ToUpperInvariant(), titleStyle);
            GUILayout.Label("Clase objetivo: " + family.MatchClass, subduedStyle);
            GUILayout.Label(count.Kills + " bajas registradas desde la actualizacion de seguimiento (v0.21).", bodyStyle);
            GUILayout.Label(count.Species.Count + " tipos de criaturas descubiertos.", bodyStyle);
            GUILayout.Space(11f);

            ChronicleTitleInfo current = null, next = null;
            foreach (ChronicleTitleInfo tier in family.Tiers)
            {
                if (MasteryState.HasTitle(playerName, tier.Text)) current = tier;
                else if (next == null) next = tier;
            }
            GUILayout.Label("MAESTRIA", headerStyle);
            if (current == null)
                GUILayout.Label("Titulo desbloqueado: ninguno registrado", bodyStyle);
            else
                GUILayout.Label("Actual: " + current.Text + "  |  Daño +" +
                    current.RewardDamagePct.ToString("0.#", CultureInfo.InvariantCulture) +
                    "%  |  Desmembramiento +" +
                    current.RewardDismemberPct.ToString("0.#", CultureInfo.InvariantCulture) + "%", bodyStyle);
            if (next != null)
                GUILayout.Label("Siguiente: " + next.Text + "  /  requisito: " + next.Threshold + " bajas (PlayerTitles)", subduedStyle);
            else
                GUILayout.Label("No hay mas titulos configurados para esta familia.", subduedStyle);

            GUILayout.Space(13f);
            GUILayout.Label("CRIATURAS DESCUBIERTAS (v0.23)", headerStyle);
            GUILayout.Label("Cada variante se cuenta por su clase real de Rebirth.", tinyStyle);
            creatureScroll = GUILayout.BeginScrollView(creatureScroll, GUILayout.ExpandHeight(true));
            List<BestiarySpeciesCount> creatures = new List<BestiarySpeciesCount>(count.Species.Values);
            creatures.Sort(delegate(BestiarySpeciesCount a, BestiarySpeciesCount b) {
                int compare = b.Kills.CompareTo(a.Kills);
                return compare != 0 ? compare : string.Compare(a.ClassName, b.ClassName, StringComparison.OrdinalIgnoreCase);
            });
            if (creatures.Count == 0)
                GUILayout.Label("Aun no se registraron criaturas en esta familia.", subduedStyle);
            foreach (BestiarySpeciesCount creature in creatures)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(creature.DisplayName + "   ·   " + creature.Kills + " bajas", bodyStyle);
                GUILayout.Label(creature.ClassName, tinyStyle);
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (panelTexture != null) UnityEngine.Object.Destroy(panelTexture);
        }
    }
}
