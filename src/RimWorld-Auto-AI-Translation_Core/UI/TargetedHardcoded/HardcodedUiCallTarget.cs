using System;
using System.Collections.Generic;
using System.Reflection;

namespace AutoTranslator_Core.TargetedHardcodedUi
{
    internal static class HardcodedUiCallTarget
    {
        private static readonly Dictionary<string, HashSet<string>> AllowedMethods =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            {
                ["Verse.Widgets"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "Label", "LabelFit", "ButtonText", "ButtonTextSubtle"
                },
                ["Verse.TooltipHandler"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "TipRegion"
                },
                ["UnityEngine.GUI"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "Label", "Button", "Box"
                }
            };

        internal static bool IsSupported(MethodBase method)
        {
            if (method == null || method.DeclaringType == null || !method.IsStatic) return false;
            HashSet<string> names;
            if (!AllowedMethods.TryGetValue(method.DeclaringType.FullName ?? string.Empty, out names) ||
                !names.Contains(method.Name ?? string.Empty))
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length > 0 &&
                   parameters[parameters.Length - 1].ParameterType == typeof(string);
        }

        internal static bool Matches(MethodBase method, HardcodedUiPatchEntry entry)
        {
            return entry != null && IsSupported(method) &&
                   string.Equals(method.DeclaringType.FullName, entry.CallDeclaringType, StringComparison.Ordinal) &&
                   string.Equals(method.Name, entry.CallMethodName, StringComparison.Ordinal) &&
                   string.Equals(HardcodedUiMethodIdentity.GetMethodSignature(method), entry.CallSignature, StringComparison.Ordinal);
        }

        internal static bool IsPlayerFacingSink(MethodBase method)
        {
            if (method == null || method.DeclaringType == null) return false;
            string type = method.DeclaringType.FullName ?? string.Empty;
            string name = method.Name ?? string.Empty;
            if (IsSupported(method)) return true;
            if (type == "Verse.Messages" && name == "Message") return true;
            if (type == "Verse.Listing_Standard" &&
                (name == "Label" || name == "CheckboxLabeled" || name == "ButtonText" ||
                 name == "TextFieldNumericLabeled" || name == "SliderLabeled")) return true;
            if (type == "Verse.Command_Action" || type == "Verse.Command_Toggle" ||
                type == "Verse.Gizmo" || type == "RimWorld.FloatMenuOption") return true;
            if (type == "Verse.Log" || type == "System.Console") return false;
            return name.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
