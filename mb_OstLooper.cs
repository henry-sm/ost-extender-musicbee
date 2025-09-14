using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using Python.Runtime;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();
        private static bool pythonInitialized = false;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "OST Looper (Local)";
            about.Description = "Finds seamless loops using a local, embedded Python engine.";
            about.Author = "Your Name";
            about.TargetApplication = ""; // No dockable panel
            about.Type = PluginType.General;
            about.VersionMajor = 1;
            about.VersionMinor = 0;
            about.Revision = 1;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents);
            about.ConfigurationPanelHeight = 0;

            if (!pythonInitialized)
            {
                string pythonHome = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "python");
                PythonEngine.PythonHome = pythonHome;
                PythonEngine.Initialize();
                pythonInitialized = true;
            }

            mbApiInterface.MB_AddMenuItem("CONTEXT/TRACK:Find Seamless Loop (Local)", "OST Looper: Analyze Track Locally", onAnalyzeTrack);
            mbApiInterface.MB_RegisterTimer(100, onTimerTick);
            return about;
        }
        
        private void onAnalyzeTrack(object sender, EventArgs e)
        {
            string[] files = null;
            mbApiInterface.Library_QueryFilesEx("domain=SelectedFiles", ref files);
            if (files == null || files.Length == 0) return;
            Task.Run(() => AnalyseTrack(files[0]));
        }

        private void AnalyseTrack(string filePath)
        {
            try
            {
                mbApiInterface.MB_SetBackgroundTaskMessage($"Analyzing (Local): {Path.GetFileName(filePath)}");

                float[] audioData = PreprocessAudio(filePath, out int sampleRate);
                if (audioData == null) throw new Exception("Could not process audio file.");

                using (Py.GIL())
                {
                    dynamic sys = Py.Import("sys");
                    string scriptDir = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "Analysis");
                    sys.path.append(scriptDir);

                    dynamic looperModule = Py.Import("looper");
                    dynamic result = looperModule.find_loop_points_from_data(audioData, sampleRate);
                    
                    string status = result.status;
                    if (status == "success")
                    {
                        mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "True");
                        mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom2, ((double)result.loop_start).ToString());
                        mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom3, ((double)result.loop_end).ToString());
                    }
                    else
                    {
                        mbApiInterface.Library_SetFileTag(filePath, MetaDataType.Custom1, "False");
                        MessageBox.Show($"Analysis failed in Python: {result.error}");
                    }
                }

                mbApiInterface.Library_CommitTagsToFile(filePath);
                mbApiInterface.MB_SetBackgroundTaskMessage("");
                MessageBox.Show("Local analysis complete!");
            }
            catch (Exception ex)
            {
                mbApiInterface.MB_SetBackgroundTaskMessage("");
                MessageBox.Show($"Analysis failed: {ex.Message}");
            }
        }
        
        private float[] PreprocessAudio(string filePath, out int actualSampleRate)
        {
            using (var reader = new AudioFileReader(filePath))
            {
                actualSampleRate = reader.WaveFormat.SampleRate;
                var sampleProvider = reader.ToMono();
                var samples = new float[sampleProvider.WaveFormat.SampleRate * (int)reader.TotalTime.TotalSeconds * sampleProvider.WaveFormat.Channels];
                int read = sampleProvider.Read(samples, 0, samples.Length);
                Array.Resize(ref samples, read);
                return samples;
            }
        }

        private void onTimerTick(object sender, EventArgs e)
        {
            if (mbApiInterface.Player_GetPlayState() != PlayState.Playing) return;
            string currentFile = mbApiInterface.Player_GetFileUrl();
            if (string.IsNullOrEmpty(currentFile)) return;

            string loopFound = mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom1);

            if (loopFound == "True")
            {
                float loopStart = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom2));
                float loopEnd = float.Parse(mbApiInterface.Library_GetFileTag(currentFile, MetaDataType.Custom3));
                float currentPosition = mbApiInterface.Player_GetPosition() / 1000f;

                if (currentPosition >= loopEnd)
                {
                    mbApiInterface.Player_SetPosition((int)(loopStart * 1000));
                }
            }
        }

        public bool Configure(IntPtr panelHandle) => false;
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() { }
        public void ReceiveNotification(string sourceFileUrl, NotificationType type) { }
    }
}