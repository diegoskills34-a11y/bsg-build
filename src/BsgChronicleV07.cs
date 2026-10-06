using System;
using System.Reflection;
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
                var harmony = new Harmony("bsg.chronicle.rebirth26.v07");
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.Out("[BSG Chronicle] v0.7 inicializada");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error inicializando: " + ex);
            }
        }
    }

    public static class ChronicleTestState
    {
        public static bool Announced;
        public static string Line = string.Empty;
        public static string Tooltip = string.Empty;
        public static XUiC_BsgChronicleFeed Controller;

        public static void Announce(EntityPlayer player)
        {
            if (Announced || player == null) return;
            Announced = true;
            Line = player.EntityName + " consiguió [CRÓNICA BSG]";
            Tooltip = "[CRÓNICA BSG]\nRequisito: eliminar 1 infectado (prueba v0.7)\nRecompensa: prueba visual de Crónica";
            Log.Out("[BSG Chronicle] " + Line);
            Refresh();
        }

        public static void Refresh()
        {
            try
            {
                if (Controller != null) Controller.RefreshBindings(true);
            }
            catch (Exception ex)
            {
                Log.Out("[BSG Chronicle] Refresh omitido: " + ex.Message);
            }
        }
    }

    public class XUiC_BsgChronicleFeed : XUiController
    {
        public override void Init()
        {
            base.Init();
            ChronicleTestState.Controller = this;
            RefreshBindings(true);
        }

        public override bool GetBindingValueInternal(ref string _value, string _bindingName)
        {
            switch (_bindingName)
            {
                case "bsgchronicle_visible":
                    _value = ChronicleTestState.Announced ? "true" : "false";
                    return true;
                case "bsgchronicle_line0":
                    _value = ChronicleTestState.Line;
                    return true;
                case "bsgchronicle_tip0":
                    _value = ChronicleTestState.Tooltip;
                    return true;
                default:
                    return base.GetBindingValueInternal(ref _value, _bindingName);
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "AwardKill", new Type[] { typeof(EntityAlive), typeof(EntityAlive) })]
    public static class ChronicleAwardKillPatch
    {
        [HarmonyPostfix]
        private static void Postfix(EntityAlive __0, EntityAlive __1)
        {
            try
            {
                if (ChronicleTestState.Announced || __0 == null || __1 == null) return;
                EntityPlayer player = __0 as EntityPlayer;
                if (player == null) return;
                if (GameManager.IsDedicatedServer) return;
                ChronicleTestState.Announce(player);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error en AwardKill: " + ex);
            }
        }
    }
}
