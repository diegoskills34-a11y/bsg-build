using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BSGBestiary
{
    // Diario de caza centrado en el jugador. La identidad XML se conserva
    // unicamente en el tracker; nunca se muestra en las fichas.
    public sealed class BsgMMOBestiaryWindow : MonoBehaviour
    {
        private static BsgMMOBestiaryWindow instance;
        private const int WindowId = 712680;
        private bool opened;
        private float openedAt;
        private int selected;
        private Vector2 familyScroll;
        private Vector2 creatureScroll;
        private Rect bounds = new Rect(75f, 48f, 1150f, 730f);
        private readonly Dictionary<string, Texture2D> portraits =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GUIStyle> styles =
            new Dictionary<string, GUIStyle>(StringComparer.OrdinalIgnoreCase);
        private Texture2D backdrop;
        private Texture2D accent;
        private Texture2D dark;
        private bool stylesReady;
        private bool loggedClose;

        private static readonly Dictionary<string, string> Display =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"bsg_infected", "Infectados"},
            {"bsg_demolisher", "Demoledores"},
            {"bsg_screamer", "Gritones"},
            {"bsg_cop", "Policías infectados"},
            {"bsg_biker", "Motociclistas"},
            {"bsg_nurse", "Enfermeras"},
            {"bsg_dogs", "Perros infectados"},
            {"bsg_bears", "Osos infectados"},
            {"bsg_radiated", "Radiados"},
            {"bsg_tainted", "Tainted"},
            {"bsg_giants", "Gigantes"},
            {"bsg_brutes", "Brutos"},
            {"bsg_kamikaze", "Kamikazes"},
            {"bsg_seekers", "Seekers"},
            {"bsg_named_bosses", "Jefes especiales"},
            {"bsg_soldiers", "Soldados infectados"}
        };

        private static readonly Dictionary<string, string> Descriptions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"bsg_infected", "Los enemigos más frecuentes. Descubrí sus distintas variantes y avanzá en tu dominio de los infectados."},
            {"bsg_demolisher", "Amenazas explosivas que requieren cuidado y precisión."},
            {"bsg_screamer", "Sus gritos pueden atraer nuevas amenazas."},
            {"bsg_cop", "Infectados de origen policial con habilidades particulares."},
            {"bsg_biker", "Enemigos resistentes que alguna vez recorrieron las rutas."},
            {"bsg_nurse", "Variantes infectadas que proceden del personal sanitario."},
            {"bsg_dogs", "Depredadores zombificados rápidos y peligrosos."},
            {"bsg_bears", "Grandes depredadores zombificados."},
            {"bsg_radiated", "Variantes radiadas. Sus maestrías no otorgan bonificaciones contra Tainted."},
            {"bsg_tainted", "Amenazas Tainted: una familia superior y distinta de los radiados."},
            {"bsg_giants", "Enormes criaturas especiales de Rebirth."},
            {"bsg_brutes", "Enemigos corpulentos y particularmente resistentes de Rebirth."},
            {"bsg_kamikaze", "Enemigos capaces de generar explosiones letales."},
            {"bsg_seekers", "Criaturas especializadas de Rebirth."},
            {"bsg_named_bosses", "Encuentros excepcionales de mayor dificultad."},
            {"bsg_soldiers", "Infectados de origen militar, incluidas sus variantes avanzadas."}
        };

        public static void Attach()
        {
            if (instance != null) return;
            try
            {
                GameObject obj = new GameObject("BSG_MMO_Bestiary");
                UnityEngine.Object.DontDestroyOnLoad(obj);
                instance = obj.AddComponent<BsgMMOBestiaryWindow>();
                Log.Out("[BSG Bestiario] v0.35: diario MMO listo, se cierra con Personaje.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Bestiario] Error inicializando diario MMO: " + ex);
            }
        }

        public static void Toggle()
        {
            if (instance == null) Attach();
            if (instance == null) return;
            if (instance.opened) { Close(); return; }
            instance.opened = true;
            instance.openedAt = Time.realtimeSinceStartup;
            instance.loggedClose = false;
            Log.Out("[BSG Bestiario] DIAG: BestiaryOverlay.Toggle => ABIERTO");
        }

        public static void Close()
        {
            if (instance == null || !instance.opened) return;
            instance.opened = false;
            Log.Out("[BSG Bestiario] DIAG: BestiaryOverlay.Close => CERRADO");
        }

        private static bool CharacterScreenVisible()
        {
            try
            {
                if (GameManager.Instance == null || GameManager.Instance.World == null)
                    return false;
                EntityPlayerLocal player =
                    GameManager.Instance.World.GetPrimaryPlayer() as EntityPlayerLocal;
                if (player == null || player.PlayerUI == null || player.PlayerUI.xui == null)
                    return false;
                XUiController frame = player.PlayerUI.xui.GetChildById("CharacterFrameWindow");
                return frame != null && frame.ViewComponent != null &&
                    frame.ViewComponent.IsVisible;
            }
            catch { return false; }
        }

        private void Update()
        {
            if (!opened) return;
            // Se deja un breve margen tras pulsar B para evitar que la
            // transicion del panel lo cierre por una animacion intermedia.
            if (Time.realtimeSinceStartup - openedAt < 0.35f) return;
            if (CharacterScreenVisible()) return;
            opened = false;
            if (!loggedClose)
            {
                loggedClose = true;
                Log.Out("[BSG Bestiario] Auto-cierre: ventana de Personaje cerrada.");
            }
        }

        private Texture2D Solid(Color color)
        {
            Texture2D result = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            result.SetPixel(0, 0, color);
            result.Apply();
            return result;
        }

        private GUIStyle Style(string name, int size, Color foreground, bool bold = false)
        {
            GUIStyle result = new GUIStyle(GUI.skin.label);
            result.fontSize = size;
            result.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            result.normal.textColor = foreground;
            result.wordWrap = true;
            result.padding = new RectOffset(2, 2, 1, 1);
            styles[name] = result;
            return result;
        }

        private GUIStyle S(string name) { return styles[name]; }

        private void EnsureStyles()
        {
            if (stylesReady) return;
            backdrop = Solid(new Color(0.045f, 0.058f, 0.071f, 0.985f));
            dark = Solid(new Color(0.09f, 0.105f, 0.125f, 0.98f));
            accent = Solid(new Color(0.65f, 0.17f, 0.14f, 1f));
            Style("title", 27, new Color(0.98f, 0.90f, 0.76f), true);
            Style("family", 23, new Color(0.98f, 0.90f, 0.76f), true);
            Style("heading", 17, Color.white, true);
            Style("body", 14, new Color(0.88f, 0.90f, 0.91f));
            Style("muted", 12, new Color(0.68f, 0.73f, 0.78f));
            Style("number", 17, new Color(0.98f, 0.79f, 0.46f), true);
            Style("unlocked", 13, new Color(0.91f, 0.79f, 0.47f), true);
            Style("locked", 13, new Color(0.72f, 0.75f, 0.79f), true);
            Style("tag", 12, new Color(0.98f, 0.90f, 0.76f));
            GUIStyle button = new GUIStyle(GUI.skin.button);
            button.fontSize = 14;
            button.alignment = TextAnchor.MiddleLeft;
            button.wordWrap = false;
            button.padding = new RectOffset(8, 4, 4, 4);
            styles["button"] = button;
            GUIStyle close = new GUIStyle(GUI.skin.button);
            close.fontSize = 13;
            close.alignment = TextAnchor.MiddleCenter;
            styles["close"] = close;
            stylesReady = true;
        }

        private static string Label(BestiaryFamilyDefinition family)
        {
            string display;
            return Display.TryGetValue(family.Id, out display) ? display : family.Name;
        }

        private static string Description(BestiaryFamilyDefinition family)
        {
            string description;
            return Descriptions.TryGetValue(family.Id, out description)
                ? description : "Registrá criaturas para descubrir nuevas variantes de esta familia.";
        }

        private Texture2D GetPortrait(string familyId)
        {
            Texture2D result;
            if (portraits.TryGetValue(familyId, out result)) return result;
            string key = familyId.StartsWith("bsg_", StringComparison.OrdinalIgnoreCase)
                ? familyId.Substring(4) : familyId;
            try
            {
                string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string filename = Path.Combine(Path.Combine(Path.Combine(root, "UI"),
                    "Portraits"), key + ".png");
                if (File.Exists(filename))
                {
                    byte[] data = File.ReadAllBytes(filename);
                    result = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    result.LoadImage(data);
                    result.filterMode = FilterMode.Bilinear;
                    result.wrapMode = TextureWrapMode.Clamp;
                    portraits[familyId] = result;
                    return result;
                }
            }
            catch (Exception ex)
            {
                Log.Out("[BSG Bestiario] Retrato ilustrativo no disponible: " + key +
                    " (" + ex.Message + ")");
            }
            // Fallback visual, para que el diario siga siendo util aun sin assets.
            result = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            int seed = familyId.GetHashCode() & 0x7fffffff;
            float hue = (seed % 360) / 360f;
            Color tint = Color.HSVToRGB(hue, 0.38f, 0.50f);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = (x - 32f) / 32f;
                    float dy = (y - 31f) / 32f;
                    float silhouette =
                        ((dx * dx / 0.48f + (dy - 0.25f) * (dy - 0.25f) / 0.70f) < 1f ||
                        (dx * dx / 0.23f + (dy + 0.30f) * (dy + 0.30f) / 0.22f) < 1f)
                        ? 0.82f : 0.22f;
                    result.SetPixel(x, y, Color.Lerp(Color.black, tint, silhouette));
                }
            result.Apply();
            portraits[familyId] = result;
            return result;
        }

        private void OnGUI()
        {
            if (!opened) return;
            try
            {
                EnsureStyles();
                float width = Mathf.Min(1150f, Screen.width - 24f);
                float height = Mathf.Min(730f, Screen.height - 20f);
                bounds = new Rect((Screen.width - width) * 0.5f,
                    (Screen.height - height) * 0.5f, width, height);
                Color oldColor = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(bounds, backdrop);
                GUI.Window(WindowId, bounds, Render, "", GUIStyle.none);
                GUI.color = oldColor;
            }
            catch (Exception ex)
            {
                opened = false;
                Log.Error("[BSG Bestiario] No se pudo mostrar la interfaz v0.35: " + ex);
            }
        }

        private string PlayerName()
        {
            try
            {
                if (GameManager.Instance != null && GameManager.Instance.World != null)
                {
                    EntityPlayer player = GameManager.Instance.World.GetPrimaryPlayer();
                    if (player != null) return player.EntityName;
                }
            }
            catch { }
            return string.Empty;
        }

        private void Render(int windowId)
        {
            List<BestiaryFamilyDefinition> families = BestiaryFamilyDefinition.Read();
            if (selected < 0 || selected >= families.Count) selected = 0;
            GUI.DrawTexture(new Rect(0, 0, bounds.width, 4), accent);
            GUILayout.BeginArea(new Rect(20, 16, bounds.width - 40, 54));
            GUILayout.BeginHorizontal();
            GUILayout.Label("BSG  /  BESTIARIO", S("title"), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("CERRAR  X", S("close"), GUILayout.Width(116),
                GUILayout.Height(34))) Close();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            float contentHeight = bounds.height - 104f;
            float leftWidth = Mathf.Min(253f, bounds.width * 0.255f);
            float rightWidth = Mathf.Min(310f, bounds.width * 0.290f);
            float centerWidth = bounds.width - leftWidth - rightWidth - 78f;
            Rect left = new Rect(20, 88, leftWidth, contentHeight);
            Rect middle = new Rect(32 + leftWidth, 88, centerWidth, contentHeight);
            Rect right = new Rect(bounds.width - rightWidth - 20, 88,
                rightWidth, contentHeight);
            GUI.DrawTexture(new Rect(right.x - 8f, 86, 1f, contentHeight), accent);

            string player = PlayerName();
            GUILayout.BeginArea(left);
            GUILayout.Label("COLECCIÓN DE CRIATURAS", S("heading"));
            GUILayout.Label("Elegí una familia para ver tu registro.", S("muted"));
            GUILayout.Space(9f);
            familyScroll = GUILayout.BeginScrollView(familyScroll, false, true);
            for (int i = 0; i < families.Count; i++)
            {
                BestiaryFamilyDefinition family = families[i];
                BestiaryFamilyCount data = BestiaryTracker.Snapshot(player, family.Id);
                GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Height(56f));
                Texture2D portrait = GetPortrait(family.Id);
                GUILayout.Label(portrait, GUILayout.Width(45f), GUILayout.Height(45f));
                string name = Label(family);
                if (GUILayout.Button((i == selected ? "▸ " : "") + name +
                    "\n" + data.Kills + " bajas", S("button"), GUILayout.Height(47f),
                    GUILayout.ExpandWidth(true)))
                {
                    selected = i;
                    creatureScroll = Vector2.zero;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUILayout.BeginArea(middle);
            if (families.Count == 0)
            {
                GUILayout.Label("No hay familias configuradas.", S("heading"));
            }
            else
            {
                RenderCollection(player, families[selected], centerWidth);
            }
            GUILayout.EndArea();

            GUILayout.BeginArea(right);
            if (families.Count > 0)
                RenderMastery(player, families[selected]);
            GUILayout.EndArea();
        }

        private void RenderCollection(string player, BestiaryFamilyDefinition family, float width)
        {
            BestiaryFamilyCount data = BestiaryTracker.Snapshot(player, family.Id);
            GUILayout.Label(Label(family).ToUpperInvariant(), S("family"));
            GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Height(145f));
            GUILayout.Label(GetPortrait(family.Id), GUILayout.Width(128f), GUILayout.Height(128f));
            GUILayout.BeginVertical();
            GUILayout.Label(Description(family), S("body"));
            GUILayout.Space(5f);
            GUILayout.Label(data.Kills + " bajas documentadas", S("number"));
            GUILayout.Label(data.Species.Count + " variantes descubiertas", S("muted"));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.Space(14f);
            GUILayout.Label("CRIATURAS DESCUBIERTAS", S("heading"));
            GUILayout.Label("Cada especie registrada aparece en tu colección.", S("muted"));
            GUILayout.Space(5f);
            creatureScroll = GUILayout.BeginScrollView(creatureScroll, false, true);
            List<BestiarySpeciesCount> species =
                new List<BestiarySpeciesCount>(data.Species.Values);
            species.Sort(delegate(BestiarySpeciesCount x, BestiarySpeciesCount y) {
                int v = y.Kills.CompareTo(x.Kills);
                return v == 0 ? string.Compare(x.DisplayName, y.DisplayName,
                    StringComparison.OrdinalIgnoreCase) : v;
            });
            if (species.Count == 0)
            {
                GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(125f));
                GUILayout.Space(10f);
                GUILayout.Label("AÚN SIN DESCUBRIMIENTOS", S("heading"));
                GUILayout.Label("Eliminá una criatura de esta familia para agregarla al registro.", S("body"));
                GUILayout.EndVertical();
            }
            foreach (BestiarySpeciesCount creature in species)
            {
                GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Height(68f));
                GUILayout.Label(GetPortrait(family.Id), GUILayout.Width(58f), GUILayout.Height(58f));
                GUILayout.BeginVertical();
                string name = string.IsNullOrEmpty(creature.DisplayName)
                    ? "Criatura desconocida" : creature.DisplayName;
                GUILayout.Label(name, S("heading"));
                GUILayout.Label(creature.Kills == 1 ? "1 baja registrada" :
                    creature.Kills + " bajas registradas", S("muted"));
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        private void RenderMastery(string player, BestiaryFamilyDefinition family)
        {
            GUILayout.Label("PROGRESO DE MAESTRÍA", S("heading"));
            GUILayout.Label("Las bonificaciones son permanentes al desbloquearse.", S("muted"));
            GUILayout.Space(8f);

            ChronicleTitleInfo next = null;
            int achieved = 0;
            for (int i = 0; i < family.Tiers.Count; i++)
            {
                ChronicleTitleInfo t = family.Tiers[i];
                if (MasteryState.HasTitle(player, t.Text)) achieved++;
                else if (next == null) next = t;
            }
            GUILayout.Label("Títulos obtenidos: " + achieved + " de " + family.Tiers.Count,
                S("number"));
            if (next == null)
                GUILayout.Label("Todos los títulos de esta familia están desbloqueados.", S("body"));
            else
                GUILayout.Label("Próximo: " + next.Text + " · " +
                    next.Threshold + " bajas requeridas", S("body"));
            GUILayout.Space(15f);

            GUILayout.Label("RECOMPENSAS Y TÍTULOS", S("heading"));
            GUILayout.Space(5f);
            for (int i = 0; i < family.Tiers.Count; i++)
            {
                ChronicleTitleInfo tier = family.Tiers[i];
                bool unlocked = MasteryState.HasTitle(player, tier.Text);
                bool prestige = i >= 3;
                string level = prestige ? "PRESTIGIO " + (i - 2) :
                    "MAESTRÍA " + (i + 1);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label((unlocked ? "✓ " : "🔒 ") + level +
                    " · " + tier.Text, unlocked ? S("unlocked") : S("locked"));
                GUILayout.Label("Requisito: " + tier.Threshold + " bajas", S("muted"));
                string bonus;
                if (prestige) bonus = "Honorífico: conserva Maestría III.";
                else if (tier.RewardDismemberPct > 0f)
                    bonus = "+" + tier.RewardDamagePct.ToString("0.#", CultureInfo.InvariantCulture) +
                        "% daño · +" + tier.RewardDismemberPct.ToString("0.#", CultureInfo.InvariantCulture) +
                        "% desmembramiento";
                else
                    bonus = "+" + tier.RewardDamagePct.ToString("0.#", CultureInfo.InvariantCulture) +
                        "% daño contra " + Label(family);
                GUILayout.Label(bonus, S("body"));
                GUILayout.EndVertical();
            }
            GUILayout.Space(10f);
            GUILayout.Label("Las bonificaciones de familias diferentes no se suman contra un mismo enemigo: se aplica la más alta.", S("muted"));
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            foreach (Texture2D tex in portraits.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            portraits.Clear();
            if (backdrop != null) UnityEngine.Object.Destroy(backdrop);
            if (dark != null) UnityEngine.Object.Destroy(dark);
            if (accent != null) UnityEngine.Object.Destroy(accent);
        }
    }
}
