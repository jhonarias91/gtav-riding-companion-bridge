using System;
using GTA;
using GTA.Native;

namespace CompanionBridge.V1
{
    public sealed class GtbikeV1CameraNative : IGtbikeV1CameraGame
    {
        public static readonly GtbikeV1Camera Shared = new GtbikeV1Camera(new GtbikeV1CameraNative());
        // GTA's bicycle camera context (OnBike = 2), not the default car/vehicle context.
        private const int BikeContext = 2;
        private const Hash GetContextMode = (Hash)0xEE778F8C7E1142E2UL;
        private const Hash SetContextMode = (Hash)0x2A2173E46DAECD12UL;
        private const Hash GetActiveContext = (Hash)0x19CAFA3C87F7C2FFUL;

        public GtbikeV1CameraReading Read()
        {
            GtbikeV1CameraReading r = new GtbikeV1CameraReading();
            Ped ped = Game.Player.Character;
            Vehicle bike = ped != null && ped.Exists() && ped.IsInVehicle() ? ped.CurrentVehicle : null;
            if (bike == null || !bike.Exists() || !Function.Call<bool>(Hash.IS_THIS_MODEL_A_BICYCLE, bike.Model.Hash)) r.Reason = "RIDER_NOT_ON_BIKE";
            else if (!Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player.Handle) || Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE)) r.Reason = "GAME_NOT_IN_CONTROL";
            else
            {
                bool cinematic = Function.Call<bool>(Hash.IS_CINEMATIC_CAM_RENDERING);
                if ((!GameplayCamera.IsRendering && !cinematic) || Function.Call<int>(GetActiveContext) != BikeContext) r.Reason = "CAPABILITY_UNAVAILABLE";
                else
                {
                    int mode = Function.Call<int>(GetContextMode, BikeContext);
                    r.Mode = cinematic ? "cinematic" : mode == 0 ? "near" : mode == 1 ? "medium" : mode == 2 ? "far" : mode == 4 ? "firstPerson" : null;
                    r.Reason = r.Mode == null ? "CAPABILITY_UNAVAILABLE" : null;
                    r.Heading = GameplayCamera.RelativeHeading; r.Pitch = GameplayCamera.RelativePitch;
                }
            }
            return r;
        }

        public void Next() { Game.SetControlValueNormalized(0, 1f); }
        public void SetMode(string mode)
        {
            Function.Call(Hash.SET_CINEMATIC_MODE_ACTIVE, mode == "cinematic");
            if (mode != "cinematic") Function.Call(SetContextMode, BikeContext, mode == "near" ? 0 : mode == "medium" ? 1 : mode == "far" ? 2 : 4);
        }
        public void Look(float heading, float pitch) { GameplayCamera.RelativeHeading = heading; GameplayCamera.RelativePitch = pitch; }
    }
}
