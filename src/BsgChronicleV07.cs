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
                ModEvents.EntityKilled.RegisterHandler(OnEntityKilled);

                var harmony = new Harmony("bsg.chronicle.rebirth26.v08");
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                Log.Out("[BSG Chronicle] v0.8 inicializada. EntityKilled registrado.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error inicializando v0.8: " + ex);
            }
        }

        private static void OnEntityKilled(ref ModEvents.SEntityKilledData data)
        {
            try
            {
                EntityPlayer player = data.KillingEntity as EntityPlayer;
                EntityAlive victim = data.KilledEntitiy as EntityAlive;

                if (player == null || victim == null) return;

                Log.Out("[BSG Chronicle] EntityKilled: " + player.EntityName + " -> " + victim.EntityName);

                if (!GameManager.IsDedicatedServer)
                    ChronicleTestState.Announce(player);
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error en ModEvents.EntityKilled: " + ex);
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
            Line = player.EntityName + " consiguió [CRONISTA BSG]";
            Tooltip = "[CRONISTA BSG]\nRequisito: eliminar 30 infectados\nRecompensa: prueba visual de Crónica";

            Log.Out("[BSG Chronicle] Publicando feed: " + Line);
            Refresh();
        }

        public static void Refresh()
        {
            try
            {
                if (Controller != null)
                    Controller.RefreshBindings();
                else
                    Log.Out("[BSG Chronicle] Feed todavía sin controlador XUi.");
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error refrescando feed: " + ex);
            }
        }
    }

    public class XUiC_BsgChronicleFeed : XUiController
    {
        public override void Init()
        {
            base.Init();
            ChronicleTestState.Controller = this;
            Log.Out("[BSG Chronicle] XUiC_BsgChronicleFeed.Init OK");
            RefreshBindings();
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

    // Fallback: algunos flujos del juego pueden pasar por AwardKill.
    // Si ModEvents.EntityKilled ya anunció, el bool Announced evita duplicados.
    [HarmonyPatch]
    public static class ChronicleAwardKillFallbackPatch
    {
        private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(GameManager).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].Name == "AwardKill")
                    yield return methods[i];
        }

        [HarmonyPostfix]
        private static void Postfix(object[] __args)
        {
            try
            {
                if (ChronicleTestState.Announced || __args == null) return;

                EntityPlayer player = null;
                for (int i = 0; i < __args.Length; i++)
                {
                    EntityPlayer p = __args[i] as EntityPlayer;
                    if (p != null)
                    {
                        player = p;
                        break;
                    }
                }

                if (player != null && !GameManager.IsDedicatedServer)
                {
                    Log.Out("[BSG Chronicle] Fallback AwardKill activado.");
                    ChronicleTestState.Announce(player);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[BSG Chronicle] Error en fallback AwardKill: " + ex);
            }
        }
    }
}
