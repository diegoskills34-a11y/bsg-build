using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using System.Text.RegularExpressions;

namespace BSGBestiary
{
    // Enriquecimiento no destructivo de la lista de PlayerTitles:
    // nunca modifica seleccion, categorias, progreso ni checks nativos.
    public static class PlayerTitlesTooltipBridge
    {
        private static DateTime nextTick = DateTime.MinValue;
        private static bool loggedRows;
        private static bool loggedMissing;
        private static readonly BindingFlags Flags = BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;

        private static string ReadText(object view, params string[] candidates)
        {
            if (view == null) return string.Empty;
            foreach (string name in candidates)
            {
                try
                {
                    PropertyInfo prop = view.GetType().GetProperty(name, Flags);
                    if (prop != null && prop.PropertyType == typeof(string) &&
                        prop.GetIndexParameters().Length == 0)
                    {
                        string value = prop.GetValue(view, null) as string;
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                    FieldInfo field = view.GetType().GetField(name, Flags);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        string value = field.GetValue(view) as string;
                        if (!string.IsNullOrEmpty(value)) return value;
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        private static bool WriteText(object view, string value)
        {
            if (view == null) return false;
            foreach (string name in new[] { "Text", "Caption" })
            {
                try
                {
                    PropertyInfo p = view.GetType().GetProperty(name, Flags);
                    if (p == null || !p.CanWrite || p.PropertyType != typeof(string) ||
                        p.GetIndexParameters().Length != 0) continue;
                    p.SetValue(view, value, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static ChronicleTitleInfo FromTooltip(string tooltip)
        {
            if (string.IsNullOrEmpty(tooltip)) return null;
            // Soportar el tooltip nativo en cualquier titulo, desbloqueado o no.
            foreach (BestiaryFamilyDefinition family in ChronicleCatalog.GetFamilies())
            {
                Match m = Regex.Match(tooltip,
                    Regex.Escape(family.Name) + @"\s*[-:]\s*rango\s*(\d+)\b",
                    RegexOptions.IgnoreCase);
                int rank;
                if (m.Success && int.TryParse(m.Groups[1].Value, out rank) &&
                    rank >= 1 && rank <= family.Tiers.Count)
                    return family.Tiers[rank - 1];
            }
            return ChronicleCatalog.Find(tooltip);
        }

        private static string BuildPreview(ChronicleTitleInfo info, string caption)
        {
            string preview = ChronicleCatalog.BuildTooltip(info.Text);
            Match progress = Regex.Match(caption ?? string.Empty, @"(\d+)\s*/\s*(\d+)");
            if (progress.Success)
                preview += "\nProgreso: " + progress.Groups[1].Value +
                    " / " + progress.Groups[2].Value;
            if (string.Equals(info.CategoryId, "bsg_test", StringComparison.OrdinalIgnoreCase))
                return preview;
            foreach (BestiaryFamilyDefinition family in ChronicleCatalog.GetFamilies())
            {
                if (!string.Equals(family.Id, info.CategoryId, StringComparison.OrdinalIgnoreCase))
                    continue;
                for (int i = 0; i < family.Tiers.Count; i++)
                {
                    if (!string.Equals(family.Tiers[i].Text, info.Text,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    if (i >= 3) return preview +
                        "\nPrestigio: mantiene Maestría III, sin bonificación adicional.";
                    return preview + "\nMaestría " + (i + 1) +
                        ": recompensa permanente; no requiere equipar este título.";
                }
            }
            return preview;
        }

        public static string DecorateNativeTooltip(string native)
        {
            if (string.IsNullOrEmpty(native) ||
                native.IndexOf("Recompensa:", StringComparison.OrdinalIgnoreCase) >= 0)
                return native;
            ChronicleTitleInfo info = FromTooltip(native);
            if (info == null) return native;
            string detailed = BuildPreview(info, string.Empty);
            if (native.IndexOf("desbloqueado", StringComparison.OrdinalIgnoreCase) >= 0)
                detailed += "\nEstado: desbloqueado";
            else if (native.IndexOf("bloqueado", StringComparison.OrdinalIgnoreCase) >= 0)
                detailed += "\nEstado: pendiente";
            return detailed.Replace("BSG", "DLG");
        }

        public static void Tick()
        {
            if (DateTime.UtcNow < nextTick) return;
            nextTick = DateTime.UtcNow.AddMilliseconds(160);
            try
            {
                if (GameManager.Instance == null || GameManager.Instance.World == null) return;
                EntityPlayerLocal player = GameManager.Instance.World.GetPrimaryPlayer() as EntityPlayerLocal;
                if (player == null || player.PlayerUI == null || player.PlayerUI.xui == null)
                    return;
                XUi xui = player.PlayerUI.xui;
                XUiController frame = xui.GetChildById("CharacterFrameWindow");
                XUiController panel = frame == null ? null : frame.GetChildById("ptTitlesPanel");
                if (panel == null) panel = xui.GetChildById("ptTitlesPanel");
                XUiController list = panel == null ? null : panel.GetChildById("ptListArea");
                if (panel == null || panel.ViewComponent == null ||
                    !panel.ViewComponent.IsVisible) return;
                int updated = 0;
                for (int i = 0; i < 100; i++)
                {
                    XUiController row = list == null ? null : list.GetChildById("ptRow" + i);
                    if (row == null && panel != null) row = panel.GetChildById("ptRow" + i);
                    if (row == null) row = xui.GetChildById("ptRow" + i);
                    if (row == null || row.ViewComponent == null ||
                        !row.ViewComponent.IsVisible) continue;
                    object view = row.ViewComponent;
                    string text = ReadText(view, "Text", "Caption", "LabelText");
                    ChronicleTitleInfo info = ChronicleCatalog.Find(text);
                    if (info == null) info = FromTooltip(row.ViewComponent.ToolTip);
                    if (info == null) continue;
                    string tooltip = BuildPreview(info, text).Replace("BSG", "DLG");
                    if (!string.Equals(row.ViewComponent.ToolTip, tooltip,
                        StringComparison.Ordinal))
                        row.ViewComponent.ToolTip = tooltip;
                    // Mostrar "/requisito" si la fila desbloqueada solo tiene el nombre.
                    // Si muestra 122/500 no se sobrescribe el contador.
                    // PlayerTitles conserva sus claves internas; solo cambiamos
                    // la cadena visible en títulos históricos de prueba.
                    if (!string.IsNullOrEmpty(text) && text.Contains("BSG"))
                        WriteText(view, text.Replace("BSG", "DLG"));
                    updated++;
                }
                if (updated > 0 && !loggedRows)
                {
                    loggedRows = true;
                    Log.Out("[DLG Títulos] v0.36: " + updated +
                        " tooltips de requisitos y recompensa asociados a las filas.");
                }
                else if (updated == 0 && !loggedMissing)
                {
                    loggedMissing = true;
                    Log.Out("[DLG Títulos] v0.36: ninguna fila identificada; " +
                        "verificar lectura XUiV_ToggleButton.Text en Rebirth.");
                }
            }
            catch (Exception ex)
            {
                if (!loggedMissing)
                {
                    loggedMissing = true;
                    Log.Out("[DLG Títulos] v0.36: aviso no fatal al enriquecer filas: " + ex);
                }
            }
        }
    }

    // PlayerTitles restablece el tooltip nativo. Este hook modifica el
    // argumento ANTES de que se almacene, en vez de competir cada 650 ms.
    // Cuando el setter no está disponible, el puente Tick sigue activo.
    [HarmonyPatch]
    public static class DlgNativeTitleTooltipPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo setter = AccessTools.PropertySetter(typeof(XUiV_ViewComponent), "ToolTip");
            if (setter != null)
            {
                Log.Out("[DLG Títulos] Hook del tooltip XUiV_ViewComponent activo.");
                yield return setter;
            }
            else
                Log.Out("[DLG Títulos] No se encontró setter de tooltip; usando puente visual.");
        }

        [HarmonyPrefix]
        private static void Prefix(ref string value)
        {
            try
            {
                if (value == null || value.IndexOf("rango", StringComparison.OrdinalIgnoreCase) < 0)
                    return;
                value = PlayerTitlesTooltipBridge.DecorateNativeTooltip(value);
            }
            catch (Exception ex)
            {
                Log.Out("[DLG Títulos] No se pudo enriquecer tooltip nativo: " + ex.Message);
            }
        }
    }
}
