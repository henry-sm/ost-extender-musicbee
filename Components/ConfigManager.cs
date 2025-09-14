using System;
using System.IO;
using System.Xml.Serialization;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// Configuration settings for the OST Extender plugin
    /// </summary>
    [Serializable]
    public class OstExtenderSettings
    {
        // Looping settings
        public bool SmartLoopEnabled { get; set; } = false;
        public bool CrossfadeEnabled { get; set; } = true;
        public int CrossfadeDuration { get; set; } = 50;
        public bool PreciseLoopingEnabled { get; set; } = true;
        
        // Analysis settings
        public bool EnableAutoAnalysis { get; set; } = false;
        public int MaxAnalysisDuration { get; set; } = 180; // Maximum track duration to analyze in seconds
    }
    
    /// <summary>
    /// Handles configuration management for the plugin
    /// </summary>
    public class ConfigManager
    {
        private readonly MusicBeeApiInterface mbApiInterface;
        private OstExtenderSettings settings;
        private string configFilePath;
        
        public ConfigManager(MusicBeeApiInterface mbApiInterface)
        {
            this.mbApiInterface = mbApiInterface;
            
            // Get MusicBee's data folder path for storing our settings
            string mbDataFolder = mbApiInterface.Setting_GetPersistentStoragePath();
            string configFolder = Path.Combine(mbDataFolder, "OstExtender");
            
            // Create the folder if it doesn't exist
            if (!Directory.Exists(configFolder))
            {
                Directory.CreateDirectory(configFolder);
            }
            
            // Set the full path to our config file
            configFilePath = Path.Combine(configFolder, "settings.xml");
            
            // Load existing settings or create default ones
            LoadSettings();
        }
        
        /// <summary>
        /// Load settings from the configuration file or create default settings if the file doesn't exist
        /// </summary>
        public void LoadSettings()
        {
            try
            {
                if (File.Exists(configFilePath))
                {
                    // Load existing settings
                    XmlSerializer serializer = new XmlSerializer(typeof(OstExtenderSettings));
                    using (FileStream fs = new FileStream(configFilePath, FileMode.Open))
                    {
                        settings = (OstExtenderSettings)serializer.Deserialize(fs);
                    }
                    mbApiInterface.MB_Trace("Settings loaded successfully");
                }
                else
                {
                    // Create default settings
                    settings = new OstExtenderSettings();
                    mbApiInterface.MB_Trace("Created default settings");
                    SaveSettings(); // Save the default settings
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error loading settings: {ex.Message}");
                
                // Create default settings if loading fails
                settings = new OstExtenderSettings();
            }
        }
        
        /// <summary>
        /// Save current settings to the configuration file
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                XmlSerializer serializer = new XmlSerializer(typeof(OstExtenderSettings));
                using (FileStream fs = new FileStream(configFilePath, FileMode.Create))
                {
                    serializer.Serialize(fs, settings);
                }
                mbApiInterface.MB_Trace("Settings saved successfully");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error saving settings: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get the current settings
        /// </summary>
        public OstExtenderSettings GetSettings()
        {
            return settings;
        }
        
        /// <summary>
        /// Update and save settings
        /// </summary>
        public void UpdateSettings(OstExtenderSettings newSettings)
        {
            settings = newSettings;
            SaveSettings();
        }
        
        /// <summary>
        /// Update a single setting value by name using reflection
        /// </summary>
        public bool UpdateSetting(string settingName, object value)
        {
            try
            {
                // Get the property info
                var propertyInfo = typeof(OstExtenderSettings).GetProperty(settingName);
                
                if (propertyInfo != null)
                {
                    // Set the value
                    propertyInfo.SetValue(settings, Convert.ChangeType(value, propertyInfo.PropertyType), null);
                    
                    // Save the updated settings
                    SaveSettings();
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error updating setting {settingName}: {ex.Message}");
                return false;
            }
        }
    }
}