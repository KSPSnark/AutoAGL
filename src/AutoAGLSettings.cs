using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AutoAGL
{
    /// <summary>
    /// User settings for this mod, accessible through the settings menu for the game.
    /// 
    /// Invaliable advice about how to make this work can be found here:
    /// https://forum.kerbalspaceprogram.com/index.php?/topic/147576-modders-notes-for-ksp-12/
    /// </summary>
    public class AutoAGLSettings : GameParameters.CustomParameterNode
    {
        public enum LandedAltimeterPreference
        {
            ASL,
            AGL
        }

        // used to indicate "behavior is disabled"
        private const int DISABLED = 0;

        // The default collision threshold, in seconds
        private const int DEFAULT_ATM_COLLISION_TIME = 10; // seconds
        private const int DEFAULT_VAC_COLLISION_TIME = 30; // seconds

        // collision-time options available on the sliders
        private static readonly int[] COLLISION_TIMES = {
            DISABLED,
            3,
            5,
            DEFAULT_ATM_COLLISION_TIME,
            15,
            20,
            DEFAULT_VAC_COLLISION_TIME,
            60,
            90,
            120
        };

        // Available collision-threshold values
        private static readonly UIValueList<int> ATM_COLLISION_THRESHOLDS = new UIValueList<int>(
            DEFAULT_ATM_COLLISION_TIME,
            FormatCollisionThreshold,
            COLLISION_TIMES);
        private static readonly UIValueList<int> VAC_COLLISION_THRESHOLDS = new UIValueList<int>(
            DEFAULT_VAC_COLLISION_TIME,
            FormatCollisionThreshold,
            COLLISION_TIMES);

        // The default altitude multiplier for armed parachutes
        private const double DEFAULT_PARACHUTE_ALTITUDE_MULTIPLIER = 2.0;

        // Available parachute altitude multipliers
        private static readonly UIValueList<double> PARACHUTE_ALTITUDE_MULTIPLIERS = new UIValueList<double>(
            DEFAULT_PARACHUTE_ALTITUDE_MULTIPLIER,
            FormatParachuteAltitudeMultiplier,
            DISABLED, 0.5, 1.0, 1.5, DEFAULT_PARACHUTE_ALTITUDE_MULTIPLIER, 3, 5);

        // GameParameters.CustomParameterNode boilerplate
        public override string Title => AutoAGL.MOD_NAME;
        public override string DisplaySection => AutoAGL.MOD_NAME;
        public override string Section => AutoAGL.MOD_NAME;
        public override int SectionOrder => 2000; // put it (probably) last
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        [GameParameters.CustomParameterUI("Enabled", toolTip = "When unchecked, totally disables AutoAGL's features")]
        public bool EnabledSetting = true;
        private const string EnabledSettingName = "EnabledSetting";

        [GameParameters.CustomParameterUI("Preference When Landed/Splashed", toolTip = "Preferred state of the altimeter when the vessel is landed or splashed.")]
        public LandedAltimeterPreference LandedSplashedSetting = LandedAltimeterPreference.ASL;

        /// <summary>
        /// Stores the selected collision threshold for atmospheric pressure.
        /// </summary>
        [GameParameters.CustomParameterUI("Collision Threshold (atmospheric)", toolTip = "Switches to AGL when terrain collision is imminent")]
        public string AtmosphericCollisionSetting = ATM_COLLISION_THRESHOLDS.DefaultLabel;
        private const string AtmosphericCollisionSettingName = "AtmosphericCollisionSetting";

        /// <summary>
        /// Stores the selected collision threshold for atmospheric pressure.
        /// </summary>
        [GameParameters.CustomParameterUI("Collision Threshold (vacuum)", toolTip = "Switches to AGL when terrain collision is imminent")]
        public string VacuumCollisionSetting = VAC_COLLISION_THRESHOLDS.DefaultLabel;
        private const string VacuumCollisionSettingName = "VacuumCollisionSetting";

        [GameParameters.CustomParameterUI("Parachute Altitude Multiplier", toolTip = "Switches to AGL when below parachute-open altitude times this value.")]
        public string ParachuteAltitudeSetting = PARACHUTE_ALTITUDE_MULTIPLIERS.DefaultLabel;
        private const string ParachuteAltitudeSettingName = "ParachuteAltitudeSetting";

        [GameParameters.CustomParameterUI("Enable path projection", toolTip = "When checked, does extra calcs to spot approaching high terrain.")]
        public bool EnablePathProjectionSetting = true;

        /// <summary>
        /// Gets whether the mod is enabled or not.
        /// </summary>
        public static bool ModEnabled
        {
            get
            {
                return Instance.EnabledSetting;
            }
        }

        /// <summary>
        /// Given an atmospheric pressure level (where 0 = vacuum, 1 = Kerbin sea-level pressure), get the
        /// number of seconds to use for terrain-collision threshold. Returns NaN if not applicable.
        /// </summary>
        /// <param name="atmosphericPressure"></param>
        /// <returns></returns>
        public static double TerrainCollisionThresholdSeconds(double atmosphericPressure)
        {
            int atmSetting = ATM_COLLISION_THRESHOLDS[Instance.AtmosphericCollisionSetting];
            int vacSetting = VAC_COLLISION_THRESHOLDS[Instance.VacuumCollisionSetting];
            bool hasAtmSetting = atmSetting != DISABLED;
            bool hasVacSetting = vacSetting != DISABLED;

            if (atmosphericPressure >= 1)
            {
                return hasAtmSetting ? atmSetting : double.NaN;
            }
            if (atmosphericPressure <= 0)
            {
                return hasVacSetting ? vacSetting : double.NaN;
            }

            // It's somewhere in between vacuum and atmospheric, so interpolate.
            return (atmosphericPressure * atmSetting)
                + ((1.0 - atmosphericPressure) * vacSetting);
        }

        /// <summary>
        /// Get the parachute altitude multiplier. Zero means "disabled".
        /// </summary>
        public static double ParachuteAltitudeMultiplier
        {
            get
            {
                return PARACHUTE_ALTITUDE_MULTIPLIERS[Instance.ParachuteAltitudeSetting];
            }
        }

        /// <summary>
        /// Gets whether we should display ASL when landed or splashed.
        /// </summary>
        public static AltimeterDisplayState LandedSplashedPreference
        {
            get
            {
                switch (Instance.LandedSplashedSetting)
                {
                    case LandedAltimeterPreference.ASL:
                        return AltimeterDisplayState.ASL;
                    case LandedAltimeterPreference.AGL:
                        return AltimeterDisplayState.AGL;
                    default:
                        return AltimeterDisplayState.DEFAULT;
                }
            }
        }

        /// <summary>
        /// Gets whether path projection is enabled.
        /// </summary>
        public static bool IsPathProjectionEnabled
        {
            get { return Instance.EnablePathProjectionSetting; }
        }

        /// <summary>
        /// Get the values for our list settings.
        /// </summary>
        /// <param name="member"></param>
        /// <returns></returns>
        public override IList ValidValues(MemberInfo member)
        {
            if (member.Name == AtmosphericCollisionSettingName) return ATM_COLLISION_THRESHOLDS.ValueLabels;
            if (member.Name == VacuumCollisionSettingName) return VAC_COLLISION_THRESHOLDS.ValueLabels;
            if (member.Name == ParachuteAltitudeSettingName) return PARACHUTE_ALTITUDE_MULTIPLIERS.ValueLabels;

            return null;
        }

        /// <summary>
        /// Used for enabling/disabling settings.
        /// </summary>
        /// <param name="member"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public override bool Enabled(MemberInfo member, GameParameters parameters)
        {
            return (member.Name == EnabledSettingName) || EnabledSetting;
        }

        /// <summary>
        /// Format terrain collision threshold values for settings display.
        /// </summary>
        /// <param name="seconds"></param>
        /// <returns></returns>
        private static string FormatCollisionThreshold(int seconds)
        {
            if (seconds == DISABLED) return "Disabled";
            return string.Format("{0}s", seconds);
        }

        /// <summary>
        /// Format parachute altitude multiplier values for settings display.
        /// </summary>
        /// <param name="factor"></param>
        /// <returns></returns>
        private static string FormatParachuteAltitudeMultiplier(double factor)
        {
            if (factor == DISABLED) return "Disabled";
            return string.Format("{0:0.0}x", factor);
        }

        /// <summary>
        /// Get the global instance of this settings object.
        /// </summary>
        private static AutoAGLSettings Instance
        {
            get { return HighLogic.CurrentGame.Parameters.CustomParams<AutoAGLSettings>(); }
        }

        private delegate string Formatter<T>(T value);

        #region ValueList
        /// <summary>
        /// Helper class for working with lists.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private class UIValueList<T>
        {
            private readonly T defaultValue;
            private readonly string defaultLabel;
            private readonly List<string> labels;
            private readonly Dictionary<string, T> valueMap;

            public UIValueList(T defaultValue, Formatter<T> formatter, params T[] values)
            {
                this.defaultValue = defaultValue;
                this.defaultLabel = formatter(defaultValue);
                labels = new List<string>(values.Length);
                valueMap = new Dictionary<string, T>();
                for (int i = 0; i < values.Length; ++i)
                {
                    T value = values[i];
                    string label = formatter(value);
                    labels.Add(label);
                    valueMap[label] = value;
                }
            }

            /// <summary>
            /// Gets the default value to use.
            /// </summary>
            public string DefaultLabel { get { return defaultLabel; } }

            /// <summary>
            /// Given a selected label, get the corresponding value.
            /// </summary>
            /// <param name="label"></param>
            /// <returns></returns>
            public T this[string label]
            {
                get
                {
                    T value;
                    return valueMap.TryGetValue(label, out value) ? value : defaultValue;
                }
            }

            /// <summary>
            /// Get the labels used for this list.
            /// </summary>
            public IList ValueLabels
            {
                get { return labels; }
            }
        }
        #endregion // UIValueList
    }
}
