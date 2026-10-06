using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

                var harmony = new Harmony("bsg.chronicle.rebirth26.v09");
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                Log.Out("[BSG Chronicle] v0.9 inicializada. La Cronica escucha directamente el desbloqueo de PlayerTitles.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error inicializando v0.9: " + ex);
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
                        ByText[text] = info;
                    }
                }

                Log.Out("[BSG Chronicle] Catalogo cargado: " + ByText.Count + " titulos.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error leyendo playertitles.xml: " + ex);
            }
        }

        private static string Attr(XElement e, string name)
        {
            XAttribute a = e.Attribute(name);
            return a == null ? string.Empty : a.Value;
        }

        public static ChronicleTitleInfo Find(string title)
        {
            if (string.IsNullOrEmpty(title)) return null;

            ChronicleTitleInfo info;
            if (ByText.TryGetValue(title, out info)) return info;

            foreach (KeyValuePair<string, ChronicleTitleInfo> pair in ByText)
            {
                if (title.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pair.Key.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pair.Value;
            }

            return null;
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

            return info.Text +
                   "\nRequisito: " + requirement +
                   "\nRecompensa: titulo " + StripBrackets(info.Text);
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
    }

    public static class ChronicleState
    {
        public static string Line = string.Empty;
        public static string Tooltip = string.Empty;
        public static XUiC_BsgChronicleFeed Controller;

        public static void Publish(string playerName, string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            if (string.IsNullOrEmpty(playerName)) playerName = "Jugador";

            Line = playerName + " consiguio " + title;
            Tooltip = ChronicleCatalog.BuildTooltip(title);

            Log.Out("[BSG Chronicle] CRONICA => " + Line);
            Log.Out("[BSG Chronicle] Tooltip => " + Tooltip.Replace("\n", " | "));

            if (Controller != null)
                Controller.ApplyNow();
            else
                Log.Out("[BSG Chronicle] El desbloqueo llego antes de inicializar la ventana; se mostrara al inicializar.");
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

                string playerName = ExtractPlayerName(args);
                if (string.IsNullOrEmpty(playerName))
                    playerName = ResolveLocalPlayerName();

                Log.Out("[BSG Chronicle] PlayerTitles desbloqueo detectado: jugador='" +
                        playerName + "' titulo='" + title + "'. " + DescribeArgs(original, args));

                ChronicleState.Publish(playerName, title);
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

        [HarmonyPostfix]
        private static void Postfix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            PlayerTitlesBridge.OnNotify(__originalMethod, __instance, __args);
        }
    }

    public class XUiC_BsgChronicleFeed : XUiController
    {
        private XUiV_Label lineLabel;
        private XUiController lineController;
        private XUiController rowController;

        public override void Init()
        {
            base.Init();

            ChronicleState.Controller = this;
            lineController = GetChildById("bsgChronicleLine0");
            rowController = GetChildById("bsgChronicleRow0");

            if (lineController != null)
                lineLabel = lineController.ViewComponent as XUiV_Label;

            Log.Out("[BSG Chronicle] XUiC_BsgChronicleFeed.Init OK. label=" +
                    (lineLabel == null ? "NO" : "SI") + " row=" + (rowController == null ? "NO" : "SI"));

            ApplyNow();
        }

        public void ApplyNow()
        {
            try
            {
                if (lineLabel == null && lineController != null)
                    lineLabel = lineController.ViewComponent as XUiV_Label;

                if (lineLabel != null)
                {
                    lineLabel.Text = ChronicleState.Line;
                    lineLabel.ToolTip = ChronicleState.Tooltip;
                }

                if (rowController != null && rowController.ViewComponent != null)
                    rowController.ViewComponent.ToolTip = ChronicleState.Tooltip;

                RefreshBindings();

                Log.Out("[BSG Chronicle] UI aplicada. texto='" + ChronicleState.Line + "'.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error aplicando texto/tooltip al XUi: " + ex);
            }
        }

        public override bool GetBindingValueInternal(ref string _value, string _bindingName)
        {
            switch (_bindingName)
            {
                case "bsgchronicle_visible":
                    _value = "true";
                    return true;
                case "bsgchronicle_line0":
                    _value = ChronicleState.Line;
                    return true;
                case "bsgchronicle_tip0":
                    _value = ChronicleState.Tooltip;
                    return true;
                default:
                    return base.GetBindingValueInternal(ref _value, _bindingName);
            }
        }
    }
}
