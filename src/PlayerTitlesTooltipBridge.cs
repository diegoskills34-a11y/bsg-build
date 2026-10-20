using System;
using System.Reflection;
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

        public static void Tick()
        {
            if (DateTime.UtcNow < nextTick) return;
            nextTick = DateTime.UtcNow.AddMilliseconds(650);
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
                if (panel == null || panel.ViewComponent == null ||
                    !panel.ViewComponent.IsVisible) return;
                int updated = 0;
                for (int i = 0; i < 100; i++)
                {
                    XUiController row = panel.GetChildById("ptRow" + i);
                    if (row == null || row.ViewComponent == null ||
                        !row.ViewComponent.IsVisible) continue;
                    object view = row.ViewComponent;
                    string text = ReadText(view, "Text", "Caption", "LabelText");
                    ChronicleTitleInfo info = ChronicleCatalog.Find(text);
                    if (info == null) info = FromTooltip(row.ViewComponent.ToolTip);
                    if (info == null) continue;
                    string tooltip = BuildPreview(info, text);
                    if (!string.Equals(row.ViewComponent.ToolTip, tooltip,
                        StringComparison.Ordinal))
                        row.ViewComponent.ToolTip = tooltip;
                    // Mostrar "/requisito" si la fila desbloqueada solo tiene el nombre.
                    // Si muestra 122/500 no se sobrescribe el contador.
                    if (string.Equals((text ?? "").Trim(), info.Text,
                        StringComparison.OrdinalIgnoreCase) && text.Length < 21 &&
                        info.Threshold > 0)
                        WriteText(view, text + " /" + info.Threshold);
                    updated++;
                }
                if (updated > 0 && !loggedRows)
                {
                    loggedRows = true;
                    Log.Out("[BSG Titulos] v0.35: " + updated +
                        " tooltips de requisitos y recompensa asociados a las filas.");
                }
                else if (updated == 0 && !loggedMissing)
                {
                    loggedMissing = true;
                    Log.Out("[BSG Titulos] v0.35: ninguna fila identificada; " +
                        "verificar lectura XUiV_ToggleButton.Text en Rebirth.");
                }
            }
            catch (Exception ex)
            {
                if (!loggedMissing)
                {
                    loggedMissing = true;
                    Log.Out("[BSG Titulos] v0.35: aviso no fatal al enriquecer filas: " + ex);
                }
            }
        }
    }
}
