using System;
using System.IO;
using System.Text.Json;

namespace Magic_Mirror
{
    public class AppSettings
    {
        public bool UseMirrorVoice { get; set; } = true;

        public bool MinimizeToTrayOnClose { get; set; } = false;
    }

    public static class SettingsManager
    {
        private static readonly string SettingsDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData
                ),
                "Magic Mirror"
            );

        private static readonly string SettingsFile =
            Path.Combine(SettingsDirectory, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(SettingsFile);

                AppSettings? settings =
                    JsonSerializer.Deserialize<AppSettings>(json);

                return settings ?? new AppSettings();
            }
            catch
            {
                // If the settings file is missing, damaged,
                // or unreadable, fall back to defaults.
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            Directory.CreateDirectory(SettingsDirectory);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string json =
                JsonSerializer.Serialize(settings, options);

            File.WriteAllText(SettingsFile, json);
        }
    }
}