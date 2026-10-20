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
        private static int LoggedClassResolveFailures;

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

            // Solo migrar el demo observado si la clase C# anterior agrupo
            // exactamente una baja; 2+ bajas serian ambiguas.
            int schema;
            if (!int.TryParse((string)xml.Root.Attribute("schema"), out schema)) schema = 1;
            if (schema < 2 && RecoverLegacyDemolisher())
            {
                Dirty = true;
                NextSaveUtc = DateTime.UtcNow;
                Log.Out("[BSG Bestiario] v0.31: Demolicion salvaje de v0.30 recuperada.");
            }
            // Cuando se añade una familia nueva, aprovechar las bajas que el
            // propio Bestiario ya registro por especie XML en bsg_infected.
            // Nunca modificar PlayerTitles ni inventar bajas previas a v0.21.
            if (schema < 3)
            {
                int restored = RecoverSoldierKills();
                if (restored > 0)
                {
                    Dirty = true;
                    NextSaveUtc = DateTime.UtcNow;
                    Log.Out("[BSG Bestiario] v0.32: " + restored +
                        " baja(s) de soldados restauradas desde Infectados.");
                }
            }
        }

        private static int RecoverSoldierKills()
        {
            int restored = 0;
            foreach (Dictionary<string, BestiaryFamilyCount> families in Players.Values)
            {
                // Nunca sumar dos veces al actualizar o reiniciar.
                if (families.ContainsKey("bsg_soldiers")) continue;
                BestiaryFamilyCount infected;
                if (!families.TryGetValue("bsg_infected", out infected)) continue;
                BestiaryFamilyCount soldiers = new BestiaryFamilyCount { Id = "bsg_soldiers" };
                foreach (BestiarySpeciesCount species in infected.Species.Values)
                {
                    if (string.IsNullOrEmpty(species.ClassName) ||
                        !species.ClassName.StartsWith("zombieSoldier",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (species.Kills <= 0) continue;
                    soldiers.Species[species.ClassName] = new BestiarySpeciesCount {
                        ClassName = species.ClassName,
                        DisplayName = species.DisplayName,
                        Kills = species.Kills
                    };
                    soldiers.Kills = (int)Math.Min((long)int.MaxValue,
                        (long)soldiers.Kills + species.Kills);
                }
                if (soldiers.Kills == 0) continue;
                families[soldiers.Id] = soldiers;
                restored += soldiers.Kills;
            }
            return restored;
        }

        private static bool RecoverLegacyDemolisher()
        {
            bool changed = false;
            foreach (Dictionary<string, BestiaryFamilyCount> families in Players.Values)
            {
                BestiaryFamilyCount infected;
                if (!families.TryGetValue("bsg_infected", out infected)) continue;
                BestiarySpeciesCount old;
                if (!infected.Species.TryGetValue("EntityZombieCopRebirth", out old)) continue;
                if (old.Kills != 1 || string.IsNullOrEmpty(old.DisplayName) ||
                    old.DisplayName.IndexOf("demolici", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                string target = old.DisplayName.IndexOf("salvaje", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "zombieDemolitionFeral" : "zombieDemolition";
                BestiaryFamilyCount demos;
                if (!families.TryGetValue("bsg_demolisher", out demos))
                {
                    demos = new BestiaryFamilyCount { Id = "bsg_demolisher" };
                    families[demos.Id] = demos;
                }
                if (demos.Kills < int.MaxValue) demos.Kills++;
                BestiarySpeciesCount found;
                if (!demos.Species.TryGetValue(target, out found))
                {
                    found = new BestiarySpeciesCount { ClassName = target, DisplayName = old.DisplayName };
                    demos.Species[target] = found;
                }
                if (found.Kills < int.MaxValue) found.Kills++;
                // Mantener exactamente el total anterior de Infectados.
                infected.Species.Remove("EntityZombieCopRebirth");
                BestiarySpeciesCount previous;
                if (!infected.Species.TryGetValue(target, out previous))
                    infected.Species[target] = new BestiarySpeciesCount {
                        ClassName = target, DisplayName = old.DisplayName, Kills = 1 };
                else if (previous.Kills < int.MaxValue) previous.Kills++;
                changed = true;
            }
            return changed;
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
                Log.Error("[BSG Bestiario] Error en EntityKilled v0.31: " + ex);
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
                XElement root = new XElement("bsgKills", new XAttribute("schema", "3"));
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
            // Rebirth comparte el CLR EntityZombieCopRebirth entre demos,
            // policias y kamikazes. Solo el nombre XML identifica a cada uno.
            try
            {
                EntityClass info = victim.EntityClass;
                if (info != null && !string.IsNullOrEmpty(info.entityClassName))
                    return info.entityClassName.Trim();
            }
            catch (Exception ex)
            {
                if (LoggedClassResolveFailures < 8)
                    Log.Out("[BSG Bestiario] EntityClass no disponible: " + ex.Message);
            }
            try
            {
                int id = victim.entityClass;
                object all = GetMember(typeof(EntityClass), null, "list", true);
                object entry = null;
                Array arr = all as Array;
                if (arr != null && id >= 0 && id < arr.Length) entry = arr.GetValue(id);
                IDictionary dic = all as IDictionary;
                if (dic != null && dic.Contains(id)) entry = dic[id];
                if (entry != null)
                {
                    object name = GetMember(entry.GetType(), entry, "entityClassName", false);
                    if (name != null && !string.IsNullOrEmpty(name.ToString()))
                        return name.ToString().Trim();
                }
            }
            catch (Exception ex)
            {
                if (LoggedClassResolveFailures < 8)
                    Log.Out("[BSG Bestiario] EntityClass por ID no disponible: " + ex.Message);
            }
            if (LoggedClassResolveFailures++ < 15)
                Log.Out("[BSG Bestiario] SIN ID XML: entityClass=" + victim.entityClass +
                        " | CLR=" + victim.GetType().Name + " | no se registra por seguridad");
            return string.Empty;
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

    // La interfaz ahora vive en BsgMMOBestiaryWindow (v0.35).
    // Conservamos este adaptador para no romper el boton B ya probado.
    public static class BestiaryOverlay
    {
        public static void Attach() { BsgMMOBestiaryWindow.Attach(); }
        public static void Toggle() { BsgMMOBestiaryWindow.Toggle(); }
        public static void Close() { BsgMMOBestiaryWindow.Close(); }
    }
}
