// Local, opt-in test trainer. No remote key endpoint; all input and native state changes run on GTBike V's Tick.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using GTA;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1KeyboardHost
    {
        public static readonly GtbikeV1KeyboardHost Shared = new GtbikeV1KeyboardHost();
        private GtbikeV1KeyboardTrainer trainer;
        private object script;
        private EventInfo tick;
        private PropertyInfo menusVisible, popupVisible, chatVisible;
        private Action<Exception> error;
        private bool toggleDown, plusDown, minusDown;
        private volatile bool requestedStop;
        private long lastFrame;
        private DateTime lastNativeTick;
        private bool? originalInvincibility;
        private int? originalWantedLevel;
        private readonly int processId = Process.GetCurrentProcess().Id;

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);

        public void Bind(Func<string, Type> resolve, bool supported, object modScript, Action<Exception> onError)
        {
            if (trainer != null || !supported || modScript == null) return;
            EventInfo targetTick = modScript.GetType().GetEvent("Tick", BindingFlags.Public | BindingFlags.Instance);
            if (targetTick == null) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            Type menus = resolve("GTBikeV.UI.Menus");
            menusVisible = menus == null ? null : menus.GetProperty("AreAnyVisible", flags);
            popupVisible = menus == null ? null : menus.GetProperty("AnyPopUpVisible", flags);
            Type multiplayer = resolve("GTBikeV.Network.Multiplayer");
            chatVisible = multiplayer == null ? null : multiplayer.GetProperty("IsChatFocused", flags);
            trainer = new GtbikeV1KeyboardTrainer(resolve, supported, modScript);
            script = modScript; tick = targetTick; error = onError;
            lastFrame = Stopwatch.GetTimestamp(); lastNativeTick = DateTime.UtcNow;
            tick.AddEventHandler(script, new EventHandler(OnModTick));
        }

        private static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        private bool Foreground()
        {
            uint id;
            GetWindowThreadProcessId(GetForegroundWindow(), out id);
            return id == (uint)processId;
        }
        private static bool Visible(PropertyInfo property) { return property != null && object.Equals(property.GetValue(null, null), true); }
        private static bool OnBicycle()
        {
            Ped rider = Game.Player.Character;
            Vehicle vehicle = rider == null || !rider.Exists() ? null : rider.CurrentVehicle;
            return vehicle != null && vehicle.Exists() && Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, vehicle.Model.Hash);
        }
        private bool InputAllowed()
        {
            Player player = Game.Player;
            return Foreground() && !Game.IsPaused && !Game.IsCutsceneActive
                && !Function.Call<bool>(Hash.GET_IS_LOADING_SCREEN_ACTIVE)
                && !Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE)
                && player != null && player.IsPlaying && player.CanControlCharacter
                && !Function.Call<bool>(Hash.IS_PLAYER_DEAD, player.Handle)
                && !Visible(menusVisible) && !Visible(popupVisible) && !Visible(chatVisible)
                && Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD) != 0;
        }

        private void OnModTick(object sender, EventArgs args)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                double dt = Math.Max(0, (double)(now - lastFrame) / Stopwatch.Frequency);
                lastFrame = now; lastNativeTick = DateTime.UtcNow;
                bool foreground = Foreground(), toggle = foreground && Down(0x79);
                bool allowed = InputAllowed();
                if (requestedStop) { requestedStop = false; trainer.Stop(); RestorePlayer(); }
                if (toggle && !toggleDown)
                {
                    if (trainer.Active) { trainer.Stop(); RestorePlayer(); Notify("Simulated smart trainer OFF. Session preserved."); }
                    else if (allowed)
                    {
                        string reason = trainer.Start();
                        if (reason == null) { originalInvincibility = Game.Player.IsInvincible; originalWantedLevel = Function.Call<int>(Hash.GET_PLAYER_WANTED_LEVEL, Game.Player.Handle); }
                        Notify(reason == null ? "SIMULATED SMART TRAINER: hold W to pedal, S to brake, +/- power, F10 to exit." : "Simulated trainer: " + reason);
                    }
                    else Notify("Close menus and return to the game before starting the simulated trainer.");
                }
                toggleDown = toggle;
                bool plus = allowed && (Down(0xBB) || Down(0x6B)), minus = allowed && (Down(0xBD) || Down(0x6D));
                if (trainer.Active && plus && !plusDown) trainer.TargetPowerW += 25;
                if (trainer.Active && minus && !minusDown) trainer.TargetPowerW -= 25;
                plusDown = plus; minusDown = minus;
                bool wasActive = trainer.Active;
                bool onBike = OnBicycle();
                // Native mounting requires zero speed/cadence. Holding W while the bike spawns must not block it.
                trainer.Step(allowed && onBike && Down(0x57), allowed && onBike && Down(0x53), allowed && onBike, dt);
                if (trainer.Active)
                {
                    if (onBike)
                    {
                        // W supplies watts to the simulated trainer rather than vanilla acceleration.
                        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 32, true);
                        Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, 71, true);
                    }
                    GTA.UI.Screen.ShowSubtitle(onBike ? "~b~SIMULATED SMART TRAINER~s~ | W: pedal | S: brake | +/-: " + trainer.TargetPowerW + " W | F10: exit"
                        : "~b~SIMULATED SMART TRAINER~s~ | On foot | F10: exit", 100);
                }
                else if (wasActive) { RestorePlayer(); Notify("Simulated smart trainer stopped. Native state restored."); }
            }
            catch (Exception ex)
            {
                try { trainer.Stop(); } catch (Exception restore) { if (error != null) error(restore); }
                try { RestorePlayer(); } catch (Exception restore) { if (error != null) error(restore); }
                if (error != null) error(ex);
                Notify("Simulated trainer stopped: " + ex.GetBaseException().Message);
            }
        }

        private static void Notify(string text) { GTA.UI.Notification.PostTicker(text, false, false); }

        private void RestorePlayer()
        {
            if (originalInvincibility == null) return;
            try
            {
                Player player = Game.Player;
                if (player == null || !player.IsPlaying) return;
                player.IsInvincible = originalInvincibility.Value;
                if (originalWantedLevel != null)
                {
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL, player.Handle, originalWantedLevel.Value, false);
                    Function.Call(Hash.SET_PLAYER_WANTED_LEVEL_NOW, player.Handle, false);
                }
                Ped rider = player.Character;
                if (rider != null && rider.Exists())
                {
                    Vehicle vehicle = rider.CurrentVehicle;
                    if (vehicle != null && vehicle.Exists() && Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, vehicle.Model.Hash))
                    { vehicle.Speed = 0f; vehicle.ForwardSpeed = 0f; }
                }
            }
            finally { originalInvincibility = null; originalWantedLevel = null; }
        }

        public void Overlay(Dictionary<string, object> snapshot) { if (trainer != null) trainer.Overlay(snapshot); }
        public bool Active { get { return trainer != null && trainer.Active; } }
        public void RequestStop() { requestedStop = true; }

        public void Watchdog()
        {
            if (Active && DateTime.UtcNow - lastNativeTick > TimeSpan.FromSeconds(10)) Abort();
        }

        public void Abort()
        {
            try { if (trainer != null) trainer.Stop(); }
            finally
            {
                try { RestorePlayer(); }
                finally
                {
                    if (tick != null && script != null) tick.RemoveEventHandler(script, new EventHandler(OnModTick));
                    trainer = null; tick = null; script = null;
                    toggleDown = false; plusDown = false; minusDown = false;
                    requestedStop = false;
                }
            }
        }
    }
}
