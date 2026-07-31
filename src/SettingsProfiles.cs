using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace LiftoffFpvGoggles
{
    /// <summary>
    /// Named sets of settings, in a file of their own next to the config.
    ///
    /// A profile holds exactly what the settings panel shows, and nothing else. That is the same
    /// rule the reset button follows, for the same reason: loading a profile must not silently
    /// change something the panel cannot show you afterwards - hidden HUD elements, key bindings,
    /// the head tracking switches. Those belong to the installation, not to a look.
    ///
    /// Values are stored the way BepInEx itself serialises them, so a float, an enum and a bool
    /// all come back as what they went in as, and the file stays readable enough to edit by hand.
    /// Renaming a profile is a matter of editing the heading in that file - which is the only way
    /// to do it, because there is no keyboard in a headset.
    /// </summary>
    internal static class SettingsProfiles
    {
        /// <summary>Not a stored profile: the values the mod ships with.</summary>
        internal const string DefaultName = "Default";

        // The two settings that are not ours. They sit in UUVR's config, so they cannot be
        // reached through our own entries - but leaving them out would mean a profile that
        // restores the analog look and then hands you back somebody else's HUD size.
        private const string ScaleKey = "UUVR::HUD Scale";
        private const string OffsetXKey = "UUVR::HUD Offset X";
        private const string OffsetYKey = "UUVR::HUD Offset Y";

        private sealed class Profile
        {
            internal string Name;
            internal readonly List<KeyValuePair<string, string>> Values =
                new List<KeyValuePair<string, string>>();
        }

        private static string FilePath()
        {
            return Path.Combine(Paths.ConfigPath, FpvGogglesPlugin.Guid + ".profiles.cfg");
        }

        // ------------------------------------------------------------------
        // Reading and writing the file
        // ------------------------------------------------------------------

        private static List<Profile> ReadAll()
        {
            List<Profile> profiles = new List<Profile>();

            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return profiles;

                Profile current = null;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        current = new Profile();
                        current.Name = line.Substring(1, line.Length - 2).Trim();
                        if (current.Name.Length > 0) profiles.Add(current);
                        continue;
                    }

                    if (current == null) continue;

                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    current.Values.Add(new KeyValuePair<string, string>(
                        line.Substring(0, split).Trim(), line.Substring(split + 1).Trim()));
                }
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Could not read the profiles file: " + e.Message);
            }

            return profiles;
        }

        private static void WriteAll(List<Profile> profiles)
        {
            try
            {
                List<string> lines = new List<string>();
                lines.Add("# Settings profiles for Liftoff FPV Goggles.");
                lines.Add("# Written by the settings menu. Rename a profile by editing its heading.");

                for (int i = 0; i < profiles.Count; i++)
                {
                    lines.Add("");
                    lines.Add("[" + profiles[i].Name + "]");

                    for (int v = 0; v < profiles[i].Values.Count; v++)
                    {
                        lines.Add(profiles[i].Values[v].Key + " = " + profiles[i].Values[v].Value);
                    }
                }

                File.WriteAllLines(FilePath(), lines.ToArray());
            }
            catch (Exception e)
            {
                FpvGogglesPlugin.Log.LogWarning("Could not write the profiles file: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // What the menu asks for
        // ------------------------------------------------------------------

        /// <summary>Every stored profile, in the order they appear in the file.</summary>
        internal static List<string> Names()
        {
            List<Profile> profiles = ReadAll();
            List<string> names = new List<string>();

            for (int i = 0; i < profiles.Count; i++) names.Add(profiles[i].Name);
            return names;
        }

        internal static bool Exists(string name)
        {
            return Names().Contains(name);
        }

        /// <summary>
        /// A name nobody has used yet. Profiles are created by saving, and saving cannot ask you
        /// to type - so they are numbered, and renamed in the file by anyone who cares to.
        /// </summary>
        internal static string NextFreeName()
        {
            List<string> taken = Names();

            for (int i = 1; i < 1000; i++)
            {
                string candidate = "Profile " + i.ToString(CultureInfo.InvariantCulture);
                if (!taken.Contains(candidate)) return candidate;
            }

            return "Profile";
        }

        /// <summary>
        /// Stores the current settings under a name, replacing that profile if it exists.
        ///
        /// The HUD size is passed in rather than read here, because while the panel is zoomed the
        /// live value is a third larger than the one you chose - and saving that would be saving
        /// something you never set.
        /// </summary>
        internal static void Save(string name, float uiScale, Vector2 uiOffset)
        {
            if (string.IsNullOrEmpty(name) || name == DefaultName) return;

            Profile profile = new Profile();
            profile.Name = name;

            ConfigFile config = FpvGogglesPlugin.Configuration;
            if (config != null)
            {
                foreach (ConfigDefinition definition in config.Keys)
                {
                    try
                    {
                        ConfigEntryBase entry = config[definition];
                        if (entry == null || !SettingsMenu.IsShown(entry)) continue;

                        profile.Values.Add(new KeyValuePair<string, string>(
                            definition.Section + "::" + definition.Key, entry.GetSerializedValue()));
                    }
                    catch (Exception) { }
                }
            }

            profile.Values.Add(new KeyValuePair<string, string>(ScaleKey, Text(uiScale)));
            profile.Values.Add(new KeyValuePair<string, string>(OffsetXKey, Text(uiOffset.x)));
            profile.Values.Add(new KeyValuePair<string, string>(OffsetYKey, Text(uiOffset.y)));

            List<Profile> profiles = ReadAll();
            int existing = IndexOf(profiles, name);

            if (existing >= 0) profiles[existing] = profile;
            else profiles.Add(profile);

            WriteAll(profiles);
            FpvGogglesPlugin.Log.LogInfo("Saved settings profile '" + name + "'.");
        }

        internal static void Delete(string name)
        {
            if (string.IsNullOrEmpty(name) || name == DefaultName) return;

            List<Profile> profiles = ReadAll();
            int existing = IndexOf(profiles, name);
            if (existing < 0) return;

            profiles.RemoveAt(existing);
            WriteAll(profiles);
            FpvGogglesPlugin.Log.LogInfo("Deleted settings profile '" + name + "'.");
        }

        /// <summary>
        /// Applies a profile, or the shipped defaults for <see cref="DefaultName"/>.
        /// </summary>
        /// <param name="uiScale">The HUD size the profile asked for, so the caller can undo a
        /// zoom against the right number.</param>
        internal static bool Apply(string name, out float uiScale, out Vector2 uiOffset)
        {
            if (string.IsNullOrEmpty(name) || name == DefaultName)
            {
                ApplyDefaults(out uiScale, out uiOffset);
                return true;
            }

            List<Profile> profiles = ReadAll();
            int index = IndexOf(profiles, name);

            if (index < 0)
            {
                FpvGogglesPlugin.Log.LogWarning("Settings profile '" + name + "' is gone; using the defaults.");
                ApplyDefaults(out uiScale, out uiOffset);
                return false;
            }

            // Started from the defaults, so a profile written before a setting existed does not
            // leave that setting on whatever the last profile happened to set it to.
            ApplyDefaults(out uiScale, out uiOffset);

            ConfigFile config = FpvGogglesPlugin.Configuration;
            List<KeyValuePair<string, string>> values = profiles[index].Values;

            for (int i = 0; i < values.Count; i++)
            {
                string key = values[i].Key;
                string value = values[i].Value;

                if (key == ScaleKey) { uiScale = Number(value, uiScale); continue; }
                if (key == OffsetXKey) { uiOffset.x = Number(value, uiOffset.x); continue; }
                if (key == OffsetYKey) { uiOffset.y = Number(value, uiOffset.y); continue; }

                if (config == null) continue;

                int split = key.IndexOf("::", StringComparison.Ordinal);
                if (split <= 0) continue;

                try
                {
                    ConfigDefinition definition = new ConfigDefinition(
                        key.Substring(0, split), key.Substring(split + 2));

                    ConfigEntryBase entry = config[definition];

                    // Settings that no longer exist are skipped rather than reported: a profile
                    // written by an older version is not an error, it is just older.
                    if (entry != null && SettingsMenu.IsShown(entry)) entry.SetSerializedValue(value);
                }
                catch (Exception) { }
            }

            FpvGogglesRunner.SetUiScale(uiScale);
            FpvGogglesRunner.SetUiOffset(uiOffset);

            FpvGogglesPlugin.Log.LogInfo("Applied settings profile '" + name + "'.");
            return true;
        }

        private static void ApplyDefaults(out float uiScale, out Vector2 uiOffset)
        {
            ConfigFile config = FpvGogglesPlugin.Configuration;
            if (config != null)
            {
                foreach (ConfigDefinition definition in config.Keys)
                {
                    try
                    {
                        ConfigEntryBase entry = config[definition];
                        if (entry == null || entry.DefaultValue == null) continue;
                        if (!SettingsMenu.IsShown(entry)) continue;

                        entry.BoxedValue = entry.DefaultValue;
                    }
                    catch (Exception) { }
                }
            }

            // Not ours to have a default for, so these are simply the values that work: the HUD
            // centred, at the size it is legible at on the plane.
            uiScale = 0.5f;
            uiOffset = Vector2.zero;

            FpvGogglesRunner.SetUiScale(uiScale);
            FpvGogglesRunner.SetUiOffset(uiOffset);
        }

        // ------------------------------------------------------------------

        private static int IndexOf(List<Profile> profiles, string name)
        {
            for (int i = 0; i < profiles.Count; i++)
            {
                if (string.Equals(profiles[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }

        // Invariant culture on both sides. A German Windows writes 0,5 and reads back 5 from
        // "0.5", which would quietly multiply the HUD by ten the first time a profile travels.
        private static string Text(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float Number(string text, float fallback)
        {
            float parsed;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return fallback;
        }
    }
}
