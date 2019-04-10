using KSP.UI.Screens.Flight;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AutoAGL
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class AutoAGL : MonoBehaviour
    {
        // The name of the mod, used for display and logging purposes.
        internal const string MOD_NAME = "AutoAGL";

        private const double KERBIN_SEALEVEL_PRESSURE_KPA = 101.325;

        // For this mod to toggle the altimeter mode, at least this much time must have
        // elapsed since the last time the mode changed. (We use this to prevent flip-flopping
        // at boundary conditions.)
        private static readonly TimeSpan MINIMUM_DWELL_TIME = new TimeSpan(0, 0, 1);

        // Interval between recalculating, to avoid excessively using CPU.
        private static readonly TimeSpan UPDATE_INTERVAL = new TimeSpan(0, 0, 0, 0, 163);

        // used to prevent thrashing
        private DateTime earliestAllowableToggleTime = DateTime.Now;

        // used to avoid spamming CPU
        private DateTime nextUpdateTime = DateTime.Now;

        // This is used for keeping track of whether the user has recently *manually* selected
        // a mode by clicking on the altimeter tumbler directly.  We need this so that this mod
        // doesn't obnoxiously override the user's manual choice.  A value of DEFAULT means "user hasn't
        // made a manual selection recently enough for us to care about", meaning that the mod has
        // license to do whatever it wants to the altimeter state.  If it's set to ASL or AGL, it
        // means "the user recently picked this state", and that then feeds into the mod's logic
        // that decides "is it cool to switch or not".
        private bool userClickedTumbler = false;
        private AltimeterDisplayState userSelection = AltimeterDisplayState.DEFAULT;

        // These are used for keeping track of the list of parachutes on the current vessel,
        // so that we can efficiently answer the question "are there any armed or deployed
        // chutes" without having to scan the entire vessel for parachutes every time (which
        // could be slow if the vessel part count is large).
        private List<ModuleParachute> parachutes = new List<ModuleParachute>();
        private uint vesselId;
        private int vesselPartCount;

        public void Awake()
        {
            Logging.Log("Registering events");
            GameEvents.OnAltimeterDisplayModeToggle.Add(OnAltimeterDisplayModeToggle);
            AltitudeTumbler.Instance.altitudeModeBtn.onClick.AddListener(OnAltitudeTumblerClick);
        }

        public void OnDestroy()
        {
            Logging.Log("Unregistering events");
            GameEvents.OnAltimeterDisplayModeToggle.Remove(OnAltimeterDisplayModeToggle);
            AltitudeTumbler.Instance.altitudeModeBtn.onClick.RemoveListener(OnAltitudeTumblerClick);
        }

        /// <summary>
        /// Here when the add-on loads upon flight start.
        /// </summary>
        public void Start()
        {
            Logging.Log("Starting");
            userSelection = AltimeterDisplayState.DEFAULT;
            userClickedTumbler = false;
            parachutes.Clear();
            vesselId = uint.MaxValue;
            vesselPartCount = -1;
        }

        /// <summary>
        /// Called on each frame.
        /// </summary>
        public void LateUpdate()
        {
            // Make sure we have a vessel.
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null) return;

            // Keep track of any vessel changes we care about.
            UpdateVessel(vessel);

            // The stuff that comes after here may potentially involve doing
            // a fair amount of CPU work, so return without doing anything unless
            // it's time to check.
            DateTime now = DateTime.Now;
            if (now < nextUpdateTime) return;
            nextUpdateTime = now + UPDATE_INTERVAL;

            if (!AutoAGLSettings.ModEnabled) return;

            // To avoid thrashing, we never tinker with the altimeter unless it's been
            // a reasonable length of time since the last time the altimeter mode changed.
            if (now < earliestAllowableToggleTime) return;

            // Now we have to balance two possibly conflicting preferences.
            // On the one hand, this mod's logic will have an idea of what the altimeter setting
            // "should" be (automatically), based on ship altitude and parachutes and such.
            // On the other hand, it's possible that the user recently *manually* selected an
            // altimeter mode, by clicking on the tumbler, and we don't want to override the
            // user's manually expressed preference.
            //
            // Here's how we resolve this conflict:  Whenever the user manually picks an altimeter
            // mode, we remember that in the userSelection variable, which is set either to ASL/AGL
            // (if the user has manually picked one of those) or DEFAULT (if the user hasn't picked
            // anything and we have free license to set it automatically).  Here's how we decide:
            //
            // 1. If userSelection is DEFAULT, just set the altimeter to whatever the auto-decider
            //    code wants it to be, and stop here.
            // 2. Otherwise, figure out what we *would* automatically set it to, if the user's
            //    manual decision didn't constrain us.
            // 3. If the indicated auto-mode is different from the user's preference... do nothing
            //    and leave the altimeter alone.
            // 4. If the indicated auto-mode *equals* the user's preference, then still do nothing
            //    to the altimeter (since they're in agreement)... but reset the userSelection flag
            //    to DEFAULT (meaning "no user preference").
            //
            // Here's an example of how that works in practice.  Let's say you're climbing from the
            // surface in AGL mode, and you *haven't* touched the altimeter toggle manually. At some
            // point you climb high enough that the mod automatically switches you to ASL mode. You then
            // decide you don't like that, and manually toggle it to AGL.  At this point, your preference
            // is stored, so the mod won't override you.  Later on, the ship descends again to the
            // point where it *would* set you to AGL if it were in auto mode.  At that point, your
            // manually-expressed preference would get reset and the mod would revert to full auto,
            // so that if the ship then climbs *again*, it would auto-switch to ASL as needed.

            string reason;
            AltimeterDisplayState currentMode = AltitudeTumbler.Instance.CurrentMode;
            AltimeterDisplayState recommendedState = RecommendAltimeterState(
                vessel,
                parachutes,
                currentMode,
                out reason);
            if (userSelection == AltimeterDisplayState.DEFAULT)
            {
                // No user selection, so just set it if it needs setting.
                if (recommendedState != currentMode)
                {
                    Logging.Log("Automatically switching altimeter to " + recommendedState + " (" + reason + ")");
                    AltitudeTumbler.Instance.SetModeTumbler(recommendedState);
                }
                return;
            }
            
            // The user picked something.  Did they pick the same as what we automatically picked, or different?
            if (userSelection == recommendedState)
            {
                // It matches, so clear the user selection if it's time yet.
                Logging.Log("Transitioned to auto-" + recommendedState + " zone, clearing user selection");
                userSelection = AltimeterDisplayState.DEFAULT;
                return;
            }

            // If we get here, it means that there's a user selection and it conflicts with the
            // auto-recommended state, so we should do nothing.
        }

        /// <summary>
        /// Here whenever the altimeter display mode gets toggled (either by the player or by this mod).
        /// </summary>
        /// <param name="data"></param>
        private void OnAltimeterDisplayModeToggle(AltimeterDisplayState mode)
        {
            Logging.Log("Altimeter mode changed to: " + mode);
            earliestAllowableToggleTime = DateTime.Now + MINIMUM_DWELL_TIME;
            if (userClickedTumbler)
            {
                userClickedTumbler = false;
                userSelection = mode;
            }
        }

        /// <summary>
        /// Here when the player deliberately toggles the altimeter mode by clicking on
        /// the altitude tumbler.
        /// </summary>
        private void OnAltitudeTumblerClick()
        {
            Logging.Log("User clicked the altitude tumbler");
            userClickedTumbler = true;
        }

        /// <summary>
        /// Keep track of the parachutes on the vessel.
        /// </summary>
        /// <param name="vessel"></param>
        private void UpdateVessel(Vessel vessel)
        {
            if ((vessel.persistentId == vesselId) && (vessel.parts.Count == vesselPartCount))
            {
                // nothing has changed, there's nothing to do
                return;
            }
            vesselId = vessel.persistentId;
            vesselPartCount = vessel.parts.Count;
            parachutes.Clear();
            for (int i = 0; i < vessel.parts.Count; ++i)
            {
                ModuleParachute chute = AsParachute(vessel.parts[i]);
                if (chute != null) parachutes.Add(chute);
            }
        }

        /// <summary>
        /// Get the ModuleParachute for a part, or null if it's not a chute.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static ModuleParachute AsParachute(Part part)
        {
            for (int i = 0; i < part.Modules.Count; ++i)
            {
                ModuleParachute chute = part.Modules[i] as ModuleParachute;
                if (chute != null) return chute;
            }
            return null; // not a parachute
        }

        /// <summary>
        /// Based on the current ship situation, determine what state the altimeter should be in.
        /// Returns DEFAULT if "not applicable" and there's no recommendation.
        /// </summary>
        /// <param name="vessel">The vessel for which we're recommending.</param>
        /// <param name="parachutes">The set of parachutes on the vessel.</param>
        /// <param name="previousState">The prior state of the altimeter.</param>
        /// <param name="reason">The reason for the recommendation. May be empty if the recommendation is the same as the previous state.</param>
        /// <returns></returns>
        private static AltimeterDisplayState RecommendAltimeterState(
            Vessel vessel,
            List<ModuleParachute> parachutes,
            AltimeterDisplayState previousState,
            out string reason)
        {
            reason = string.Empty;

            // Are we landed?
            switch (vessel.situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.PRELAUNCH:
                case Vessel.Situations.SPLASHED:
                    reason = "on surface";
                    return AutoAGLSettings.LandedSplashedPreference;
            }

            // Check for imminent terrain collision.
            double clearance = CalculateGroundClearance(vessel);
            double collisionThresholdSeconds = AutoAGLSettings.TerrainCollisionThresholdSeconds(Atmospheres(vessel));
            if (!double.IsNaN(collisionThresholdSeconds))
            {
                double collisionSeconds = CalculateTimeUntilImpact(vessel, clearance);
                if (collisionSeconds < collisionThresholdSeconds)
                {
                    if (previousState != AltimeterDisplayState.AGL)
                    {
                        if (vessel.verticalSpeed < 0)
                        {
                            reason = string.Format(
                                "< {0:0.0}s from terrain, {1:0}m @ {2:0} m/s",
                                collisionThresholdSeconds,
                                clearance,
                                -vessel.verticalSpeed);
                        }
                        else
                        {
                            reason = string.Format(
                                "< {0:0.0}s from terrain, {1:0}m",
                                collisionThresholdSeconds,
                                clearance);
                        }
                    }
                    return AltimeterDisplayState.AGL;
                }
            }

            // Check for deployed chute
            double parachuteAltitude = double.NaN;
            if (AutoAGLSettings.ParachuteAltitudeMultiplier > 0)
            {
                parachuteAltitude = CalculateParachuteActivationAltitude(parachutes);
                if (clearance < parachuteAltitude * AutoAGLSettings.ParachuteAltitudeMultiplier)
                {
                    if (previousState != AltimeterDisplayState.AGL)
                    {
                        reason = string.Format("< {0:0.0}x parachute's {1}m", AutoAGLSettings.ParachuteAltitudeMultiplier, parachuteAltitude);
                    }
                    return AltimeterDisplayState.AGL;
                }
            }

            // None of the above apply, so prefer ASL by default.
            if (previousState != AltimeterDisplayState.ASL)
            {
                if (!double.IsNaN(collisionThresholdSeconds))
                {
                    reason = string.Format(
                        "> {0:0.0}s from terrain, {1:0}m",
                        collisionThresholdSeconds,
                        clearance);
                }
                else if (!double.IsNaN(parachuteAltitude))
                {
                    reason = (parachuteAltitude > 0)
                        ? string.Format("> {0:0.0}x parachute's {1}m", AutoAGLSettings.ParachuteAltitudeMultiplier, parachuteAltitude)
                        : "no active chutes";
                }
                else
                {
                    reason = "all checks disabled";
                }
            }
            return AltimeterDisplayState.ASL;
        }

        /// <summary>
        /// If the current vessel has any deployed or armed parachutes, finds the highest value for
        /// height-above-terrain of any of the deployed or armed chutes, in meters. If there
        /// aren't any, returns zero.
        /// </summary>
        /// <returns></returns>
        private static double CalculateParachuteActivationAltitude(List<ModuleParachute> parachutes)
        {
            double highestAltitudeFound = 0;
            for (int i = 0; i < parachutes.Count; ++i)
            {
                double partAltitude = ActivationAltitudeOf(parachutes[i]);
                if (partAltitude > highestAltitudeFound) highestAltitudeFound = partAltitude;
            }
            return highestAltitudeFound;
        }

        /// <summary>
        /// Given a parachute, check to see whether it's deployed-or-armed, and if so, return
        /// the height-above-terrain activation altitude in meters.  Otherwise, return zero.
        /// </summary>
        /// <param name="part"></param>
        /// <returns></returns>
        private static double ActivationAltitudeOf(ModuleParachute chute)
        {
            switch (chute.deploymentState)
            {
                case ModuleParachute.deploymentStates.ACTIVE:
                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                case ModuleParachute.deploymentStates.DEPLOYED:
                    return chute.deployAltitude;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Gets the number of seconds until we would impact terrain if we were in free fall.
        /// Returns infinity for "too far in future to care about".
        /// </summary>
        /// <param name="clearance">Vessel's ground clearance in meters</param>
        private static double CalculateTimeUntilImpact(Vessel vessel, double clearance)
        {
            if (double.IsInfinity(clearance)) return double.PositiveInfinity;

            // Exclude cases where meeting the surface can't happen
            switch (vessel.situation)
            {
                case Vessel.Situations.ESCAPING:
                case Vessel.Situations.ORBITING:
                    if (vessel.verticalSpeed > 0) return double.PositiveInfinity;
                    double periapsis = vessel.orbit.semiMajorAxis * (1.0 - vessel.orbit.eccentricity);
                    if (periapsis > vessel.mainBody.Radius) return double.PositiveInfinity;
                    break;
                case Vessel.Situations.FLYING:
                case Vessel.Situations.SUB_ORBITAL:
                    break;
                default:
                    return double.PositiveInfinity;
            }

            // Work out centripetal acceleration (i.e. an upward acceleration component
            // due to the planet curving away from us)
            Vector3 shipPosition = vessel.transform.position - vessel.mainBody.position;
            double lateralSpeed = Vector3.Cross(shipPosition.normalized, vessel.obt_velocity).magnitude;
            double centripetalAcceleration = lateralSpeed * lateralSpeed / shipPosition.magnitude;

            // We now know how far we have to fall.  What's our downward acceleration? (might be negative)
            // Note that we deliberately exclude the ship's own propulsion. Including that causes a confusing
            // UI experience.
            double downwardAcceleration = vessel.graviticAcceleration.magnitude - centripetalAcceleration;

            // If our downward acceleration is negative (i.e. net acceleration is upward), there's
            // a chance we may not be due to hit the ground at all. Check for that.
            double fallSpeed = Math.Max(0, -vessel.verticalSpeed);
            if (downwardAcceleration < 0)
            {
                double maxFallDistance = -(fallSpeed * fallSpeed) / (2.0 * downwardAcceleration);
                if (maxFallDistance < (clearance + 1.0)) return double.PositiveInfinity;
            }

            // solve the quadratic equation
            double secondsUntilImpact = (-fallSpeed + Math.Sqrt(fallSpeed * fallSpeed + 2.0 * downwardAcceleration * clearance)) / downwardAcceleration;

            return secondsUntilImpact;
        }

        /// <summary>
        /// Gets the vessel's current height above terrain, in meters.
        /// Returns infinity for "too high to bother figuring out".
        /// </summary>
        /// <returns></returns>
        private static double CalculateGroundClearance(Vessel vessel)
        {
            // How high above terrain are we?
            double clearance = vessel.altitude - vessel.pqsAltitude;

            // If we're over water, use water surface instead of ocean floor.
            if (vessel.mainBody.ocean && (clearance > vessel.altitude))
            {
                clearance = vessel.altitude;
            }

            return clearance;
        }

        /// <summary>
        /// Gets the pressure at surface level (i.e. location directly beneath the ship), normalized to
        /// 0 = vacuum, 1 = Kerbin sea-level pressure.
        /// </summary>
        /// <param name="vessel"></param>
        /// <returns></returns>
        private static double Atmospheres(Vessel vessel)
        {
            if (!vessel.mainBody.atmosphere) return 0;
            double surfaceHeight = vessel.pqsAltitude;
            if (vessel.mainBody.ocean && surfaceHeight < 0) surfaceHeight = 0;
            return vessel.mainBody.GetPressureAtm(surfaceHeight);
        }
    }
}
