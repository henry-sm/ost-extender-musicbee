using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using static MusicBeePlugin.Plugin;

namespace MusicBeePlugin
{
    /// <summary>
    /// Manages the plugin's user interface components
    /// </summary>
    public class UserInterface
    {
        private readonly MusicBeeApiInterface mbApiInterface;
        private readonly PlaybackController playbackController;
        private readonly ConfigManager configManager;
        
        public UserInterface(MusicBeeApiInterface mbApiInterface, 
                            PlaybackController playbackController,
                            ConfigManager configManager)
        {
            this.mbApiInterface = mbApiInterface;
            this.playbackController = playbackController;
            this.configManager = configManager;
        }
        
        /// <summary>
        /// Initialize plugin menu items
        /// </summary>
        public void InitializeMenuItems()
        {
            // Add menu items to Tools menu
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender", "OST Extender", null);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Analyze Track", "Find Loop Points", OnAnalyzeTrack);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Play with Loop", "Enable Smart Looping", OnSmartLoop);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Create Extended Version", "Extend OST (A+B×5)", OnExtendTrack);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Toggle Loop", "Toggle Smart Loop On/Off", OnToggleSmartLoop);
            
            // Advanced settings submenu
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Settings", "Settings...", null);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Settings/Toggle Crossfade", "Crossfade Between Loop Points", OnToggleCrossfade);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Settings/Toggle Precise Looping", "Precise Sample-Accurate Looping", OnTogglePreciseLooping);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Settings/Set Crossfade Duration", "Set Crossfade Duration...", OnSetCrossfadeDuration);
            mbApiInterface.MB_AddMenuItem("mnuTools/OST Extender/Settings/Re-analyze All OSTs", "Re-analyze All OSTs in Library...", OnReanalyzeAll);

            // Add to multiple context menus to ensure it appears on right-click
            string[] contextMenus = new[] {
                "mnuTracklist", 
                "mnuTracklistNoSelection",
                "mnuFileListSongList", 
                "mnuFileListSongListNoSelection",
                "mnuContext" 
            };
            
            foreach (string menuPath in contextMenus)
            {
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender", "OST Extender", null);
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender/Analyze Track", "Find Loop Points", OnAnalyzeTrack);
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender/Play with Loop", "Enable Smart Looping", OnSmartLoop);
                mbApiInterface.MB_AddMenuItem($"{menuPath}/OST Extender/Create Extended Version", "Extend OST", OnExtendTrack);
            }
        }
        
        /// <summary>
        /// Show a message box on the main thread
        /// </summary>
        public void ShowMessageBox(string message, string title = "OST Extender", MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            Form mainForm = Form.FromHandle(mbApiInterface.MB_GetWindowHandle()).FindForm();
            mainForm.Invoke(new Action(() => {
                MessageBox.Show(mainForm, message, title, MessageBoxButtons.OK, icon);
            }));
        }
        
        /// <summary>
        /// Handle analyze track menu click
        /// </summary>
        private void OnAnalyzeTrack(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
            if (files == null || files.Length == 0)
            {
                ShowMessageBox("Please select a track to analyze.", "OST Extender", MessageBoxIcon.Warning);
                return;
            }
            
            // Get the selected file path
            string filePath = files[0];
            
            // Notify user that analysis has started
            mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing: {System.IO.Path.GetFileName(filePath)}");
            
            // We'll raise an event that will be handled by the main Plugin class
            AnalyzeTrackRequested?.Invoke(filePath);
        }
        
        /// <summary>
        /// Handle smart loop menu click
        /// </summary>
        private void OnSmartLoop(object sender, EventArgs e)
        {
            try
            {
                string[] files = null;
                mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
                if (files == null || files.Length == 0)
                {
                    ShowMessageBox("Please select a track first.");
                    return;
                }
                
                string filePath = files[0];
                
                if (playbackController.IsTrackLoopable(filePath))
                {
                    // Get loop points from metadata
                    float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom2));
                    float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom3));
                    
                    // Enable smart loop
                    if (playbackController.EnableSmartLoop(filePath))
                    {
                        // Format time values for display
                        string formattedStart = FormatTime(loopStart);
                        string formattedEnd = FormatTime(loopEnd);
                        string formattedLength = FormatTime(loopEnd - loopStart);
                        
                        // Show confirmation with clear visual feedback
                        ShowMessageBox($"Smart Loop Enabled!\n\nTrack will loop from {formattedStart} to {formattedEnd}.\n" +
                                     $"Loop Length: {formattedLength}\n\n" +
                                     "When playback reaches the end point, it will automatically jump back to the start point.");
                        
                        // Stop current playback
                        mbApiInterface.Player_Stop();
                        
                        // Queue and play the track
                        mbApiInterface.NowPlayingList_Clear();
                        mbApiInterface.NowPlayingList_QueueNext(filePath);
                        mbApiInterface.Player_PlayNextTrack();
                    }
                }
                else
                {
                    // Check if the track has been analyzed
                    string loopFlag = mbApiInterface.Library_GetFileTag(filePath, MetaDataType.Custom1);
                    
                    if (loopFlag == null || loopFlag == "")
                    {
                        // Track hasn't been analyzed at all
                        if (MessageBox.Show("This track hasn't been analyzed yet. Would you like to analyze it now?",
                                         "OST Extender", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            // Run the analysis first
                            AnalyzeTrackRequested?.Invoke(filePath);
                            
                            // Set a timer to enable smart loop after analysis
                            Task.Run(() => 
                            {
                                System.Threading.Thread.Sleep(2000); // Wait for analysis to start
                                mbApiInterface.MB_Trace("Will enable loop after analysis completes");
                            });
                        }
                    }
                    else
                    {
                        // Track was analyzed but no loop was found
                        ShowMessageBox("No loop points were found for this track during analysis.\n\nPlease re-analyze the track or use the 'Create Extended Version' option instead.", 
                                     "OST Extender", MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_Trace($"Error handling smart loop menu: {ex.Message}");
                ShowMessageBox($"Error enabling smart loop: {ex.Message}", "OST Extender", MessageBoxIcon.Error);
            }
        }
        
        /// <summary>
        /// Format time in seconds to a readable MM:SS.T format
        /// </summary>
        private string FormatTime(float seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}.{2:D1}", (int)time.TotalMinutes, time.Seconds, time.Milliseconds / 100);
        }

        /// <summary>
        /// Handle toggle smart loop menu click
        /// </summary>
        private void OnToggleSmartLoop(object sender, EventArgs e)
        {
            playbackController.ToggleSmartLoop();
            string status = playbackController.IsSmartLoopEnabled() ? "enabled" : "disabled";
            ShowMessageBox($"Smart Loop is now {status}");
            
            // Save the setting
            var settings = configManager.GetSettings();
            settings.SmartLoopEnabled = playbackController.IsSmartLoopEnabled();
            configManager.UpdateSettings(settings);
        }

        /// <summary>
        /// Handle extend track menu click
        /// </summary>
        private void OnExtendTrack(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", out files);
            if (files == null || files.Length == 0)
            {
                ShowMessageBox("Please select a track first.");
                return;
            }
            
            // Get the selected file path
            string filePath = files[0];
            
            if (playbackController.IsTrackLoopable(filePath))
            {
                ExtendTrackRequested?.Invoke(filePath);
            }
            else
            {
                ShowMessageBox("This track doesn't have loop points. Please analyze it first.", 
                             "OST Extender", MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Handle toggle crossfade menu click
        /// </summary>
        private void OnToggleCrossfade(object sender, EventArgs e)
        {
            playbackController.ToggleCrossfade();
            
            // Save the setting
            var settings = configManager.GetSettings();
            settings.CrossfadeEnabled = !settings.CrossfadeEnabled;
            configManager.UpdateSettings(settings);
        }

        /// <summary>
        /// Handle toggle precise looping menu click
        /// </summary>
        private void OnTogglePreciseLooping(object sender, EventArgs e)
        {
            playbackController.TogglePreciseLooping();
            
            // Save the setting
            var settings = configManager.GetSettings();
            settings.PreciseLoopingEnabled = !settings.PreciseLoopingEnabled;
            configManager.UpdateSettings(settings);
        }

        /// <summary>
        /// Handle set crossfade duration menu click
        /// </summary>
        private void OnSetCrossfadeDuration(object sender, EventArgs e)
        {
            // Create a simple dialog to get crossfade duration
            Form inputDialog = new Form();
            inputDialog.Text = "Set Crossfade Duration";
            inputDialog.StartPosition = FormStartPosition.CenterParent;
            inputDialog.Width = 300;
            inputDialog.Height = 150;
            inputDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            inputDialog.MaximizeBox = false;
            inputDialog.MinimizeBox = false;
            
            Label label = new Label();
            label.Text = "Crossfade duration in milliseconds (0-500):";
            label.SetBounds(10, 10, 280, 20);
            
            NumericUpDown numUpDown = new NumericUpDown();
            numUpDown.SetBounds(10, 40, 280, 20);
            numUpDown.Minimum = 0;
            numUpDown.Maximum = 500;
            numUpDown.Value = configManager.GetSettings().CrossfadeDuration;
            
            Button okButton = new Button();
            okButton.Text = "OK";
            okButton.DialogResult = DialogResult.OK;
            okButton.SetBounds(110, 80, 75, 23);
            
            Button cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.SetBounds(190, 80, 75, 23);
            
            inputDialog.Controls.Add(label);
            inputDialog.Controls.Add(numUpDown);
            inputDialog.Controls.Add(okButton);
            inputDialog.Controls.Add(cancelButton);
            inputDialog.AcceptButton = okButton;
            inputDialog.CancelButton = cancelButton;
            
            if (inputDialog.ShowDialog() == DialogResult.OK)
            {
                int duration = (int)numUpDown.Value;
                playbackController.SetCrossfadeDuration(duration);
                
                // Save the setting
                var settings = configManager.GetSettings();
                settings.CrossfadeDuration = duration;
                configManager.UpdateSettings(settings);
            }
        }

        /// <summary>
        /// Handle re-analyze all menu click
        /// </summary>
        private void OnReanalyzeAll(object sender, EventArgs e)
        {
            if (MessageBox.Show("This will re-analyze all tracks in your library that have been previously " +
                              "processed with OST Extender. This can take a long time. Continue?",
                              "OST Extender", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                ReanalyzeAllRequested?.Invoke();
            }
        }
        
        // Events for communication with the main plugin class
        public event Action<string> AnalyzeTrackRequested;
        public event Action<string> ExtendTrackRequested;
        public event Action ReanalyzeAllRequested;
    }
}