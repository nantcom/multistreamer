using Ionic.Zip;
using NantCom.MultiStreamer.Properties;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using Vlc.DotNet.Wpf;
using DotNetSettings = NantCom.MultiStreamer.Properties.Settings;

namespace NantCom.MultiStreamer.Data
{
    public class ProjectData : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        private void OnPropertyChanged(string property)
        {
            this.PropertyChanged(this, new PropertyChangedEventArgs(property));
        }

        /// <summary>
        /// Settings
        /// </summary>
        public dynamic Settings
        {
            get
            {
                return DotNetSettings.Default;
            }
        }

        /// <summary>
        /// Running in Test Mode
        /// </summary>
        public bool IsTestMode { get; set; }

        #region Status

        /// <summary>
        /// File being downloaded
        /// </summary>
        public string StatusText { get; set; }

        /// <summary>
        /// Current Download Progress
        /// </summary>
        public double StatusProgress { get; set; }

        /// <summary>
        /// Whether status is unknown
        /// </summary>
        public bool StatusUnknown { get; set; }

        /// <summary>
        /// Set the status message
        /// </summary>
        /// <param name="text"></param>
        /// <param name="progress"></param>
        /// <param name="isUnknown"></param>
        public void SetStatus( string text = null, double progress = 0, bool isUnknown = false)
        {
            this.StatusText = text;
            this.StatusProgress = progress;
            this.StatusUnknown = isUnknown;

            this.OnPropertyChanged("StatusText");
            this.OnPropertyChanged("StatusProgress");
            this.OnPropertyChanged("StatusUnknown");
        }

        #endregion

        #region Download

        /// <summary>
        /// Downloads and extract given url
        /// </summary>
        /// <param name="folder">Relative path to this application</param>
        private async Task DownloadAndExtract(string url)
        {
            WebClient client = new WebClient();
            client.DownloadProgressChanged += (s, e) =>
            {
                this.SetStatus("Downloading..." + url, e.ProgressPercentage / 100d);
            };

            var file = Path.GetFileName(url);
            var path = Path.Combine(this.CurrentFolder, file);

            if (File.Exists(path) == false)
            {
                await client.DownloadFileTaskAsync(url, path);
            }
            
            try
            {
                await Task.Run(() =>
                {
                    ZipFile zip = ZipFile.Read(path);
                    zip.ExtractProgress += (s, e) =>
                    {
                        if (e.EventType == ZipProgressEventType.Extracting_AfterExtractEntry)
                        {
                            this.SetStatus("Extracting... " + e.CurrentEntry.FileName, e.EntriesExtracted / (double)e.EntriesTotal);
                        }
                    };
                    zip.ExtractAll(this.CurrentFolder, ExtractExistingFileAction.OverwriteSilently);
                    zip.Dispose();
                });
            }
            catch (Exception)
            {
            }

            this.SetStatus();

            File.Delete(path);
        }

        #endregion

        #region OBS

        /// <summary>
        /// List of all OBS Profiles
        /// </summary>
        public string[] OBSProfiles { get; set; }

        /// <summary>
        /// The selected obs profile
        /// </summary>
        public string OBSProfileSelected { get; set; }

        /// <summary>
        /// Whether ons is running
        /// </summary>
        public bool IsObsRunning
        {
            get
            {
                return Process.GetProcessesByName("obs32").Length > 0 ||
                    Process.GetProcessesByName("obs64").Length > 0;
            }
        }

        public bool IsOBSAvailable
        {
            get
            {
                return  Directory.Exists(this.OBSProfileFolder);
            }
        }

        /// <summary>
        /// Lists the obs profile
        /// </summary>
        public async void ListOBSProfiles()
        {
            await Task.Run(() =>
            {
                this.OBSProfiles = Directory.GetDirectories(this.OBSProfileFolder).Select(s => Path.GetFileName(s)).ToArray();
                this.OnPropertyChanged("OBSProfiles");

                this.OBSProfileSelected = this.OBSProfiles.FirstOrDefault();
            });
        }

        /// <summary>
        /// Modify the obs profile to support streaming to our nginx server
        /// </summary>
        /// <param name="profileName"></param>
        private async Task StartOBS(string profileName)
        {
            await Task.Run(() =>
            {
                var profilePath = Path.Combine(this.OBSProfileFolder, this.OBSProfileSelected, "service.json");
                var serviceJson = File.ReadAllText(profilePath);

                dynamic profile = JObject.Parse(serviceJson);
                profile.type = "rtmp_custom";
                profile.settings.key = "1";
                profile.settings.server = "rtmp://localhost/ingest";

                File.WriteAllText(profilePath, (profile as JObject).ToString());
                
                var pi = new ProcessStartInfo();
                pi.FileName = @"C:\Program Files (x86)\obs-studio\bin\64bit\obs64.exe";
                pi.WorkingDirectory = @"C:\Program Files (x86)\obs-studio\bin\64bit";
                Process.Start(pi);
            });
        }

        #endregion

        #region NginX

        private string NginXTemplate = @"

worker_processes  1;

error_log  logs/error.log debug;

events {
    worker_connections  1024;
}

rtmp {
    server {
        listen 1935;
		chunk_size 4096;

        application ingest {
            live on;

			%TWITCH%
			%YOUTUBE%
        }
		
        application tofacebook {
            live on;		

			%FACEBOOK%
        }
    }
	
}

http {
    server {
        listen      8080;
		
        location / {
            root html;
        }
		
        location /stat {
            rtmp_stat all;
            rtmp_stat_stylesheet stat.xsl;
        }

        location /stat.xsl {
            root html;
        }
		
    }
}

";
        /// <summary>
        /// Whether NginX is running
        /// </summary>
        public bool IsNginXRunning
        {
            get
            {
                return Process.GetProcessesByName("nginx").Length > 0;
            }
        }

        private bool _Already = false;
        private void ListenToExit()
        {
            if (_Already)
            {
                return;
            }

            Application.Current.Exit += (s, e) =>
            {
                this.NginxKill();
            };

            _Already = true;
        }

        /// <summary>
        /// Kill all nginx processes
        /// </summary>
        private void NginxKill()
        {
            if (Directory.Exists(Path.Combine(this.CurrentFolder, "nginx-rtmp-win32-master")))
            {
                ProcessStartInfo pi = new ProcessStartInfo();
                pi.FileName = Path.Combine(this.CurrentFolder, @"nginx-rtmp-win32-master\nginx.exe");
                pi.Arguments = "-s stop";
                pi.RedirectStandardError = true;
                pi.RedirectStandardOutput = true;
                pi.UseShellExecute = false;
                pi.CreateNoWindow = true;
                pi.WorkingDirectory = Path.Combine(this.CurrentFolder, @"nginx-rtmp-win32-master");

                Process.Start(pi).WaitForExit();
            }

            foreach (var process in Process.GetProcessesByName("nginx"))
            {
                process.Kill();
            }
        }

        /// <summary>
        /// Writes NginxX config and start
        /// </summary>
        private async Task NginxStart()
        {
            int serviceCount = 0;

            this.NginxKill();
            
            if (Directory.Exists(Path.Combine(this.CurrentFolder, "nginx-rtmp-win32-master")) == false)
            {
                await this.DownloadAndExtract("https://github.com/illuspas/nginx-rtmp-win32/archive/master.zip");
            }

            var sb = new StringBuilder(this.NginXTemplate);

            if (this.IsTestMode == false && !string.IsNullOrEmpty(DotNetSettings.Default.TwitchStreamKey))
            {
                serviceCount++;

                this.SetStatus("Determining Best Twitch Server...", 0, true);

                var bestTwitchServer = await this.GetBestTwitchServer();
                sb.Replace("%TWITCH%", "push " + bestTwitchServer.Replace("{stream_key}", DotNetSettings.Default.TwitchStreamKey) + ";");
            }
            else
            {
                sb.Replace("%TWITCH%", null);
            }

            if (this.IsTestMode == false && !string.IsNullOrEmpty(DotNetSettings.Default.YouTubeStreamKey))
            {
                serviceCount++;
                sb.Replace("%YOUTUBE%", "push rtmp://a.rtmp.youtube.com/live2/" + DotNetSettings.Default.YouTubeStreamKey + ";");
            }
            else
            {
                sb.Replace("%YOUTUBE%", null);
            }

            if (this.IsTestMode == false && !string.IsNullOrEmpty(DotNetSettings.Default.FacebookStreamKey))
            {
                serviceCount++;
                sb.Replace("%FACEBOOK%", "push rtmp://live-api.facebook.com:80/rtmp/" + DotNetSettings.Default.FacebookStreamKey + ";");
            }
            else
            {
                sb.Replace("%FACEBOOK%", null);
            }

            this.SetStatus("Starting Nginx", 0, true);

            bool exited = false;
            string exitmessage = null;
            await Task.Run(() =>
            {
                File.WriteAllText(Path.Combine(this.CurrentFolder, @"nginx-rtmp-win32-master\conf\nginx.conf"), sb.ToString());

                ProcessStartInfo pi = new ProcessStartInfo();
                pi.FileName = Path.Combine(this.CurrentFolder, @"nginx-rtmp-win32-master\nginx.exe");
                pi.RedirectStandardError = true;
                pi.RedirectStandardOutput = true;
                pi.UseShellExecute = false;
                pi.CreateNoWindow = true;
                pi.WorkingDirectory = Path.Combine(this.CurrentFolder, @"nginx-rtmp-win32-master");

                var process = Process.Start(pi);
                exited = process.WaitForExit(2000);

                if (exited)
                {
                    exitmessage = process.StandardError.ReadToEnd();
                }

            });
            
            if (exited)
            {
                MessageBox.Show(
                    "NginX as exited unexpectedly. This is what it has to say:\r\n" +
                        exitmessage, "Nginx failed to start.",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                    
            }

            this.SetStatus();
            this.ListenToExit();
        }

        #endregion

        #region Twitch Servers

        /// <summary>
        /// Check for Twitch Server
        /// </summary>
        private async Task<string> GetBestTwitchServer()
        {
            RestClient client = new RestClient("https://ingest.twitch.tv/");
            RestRequest req = new RestRequest("/ingests");
            req.AddHeader("Client-ID", "fupq1kum0evpdxqj5llp8z62f3yji6");
            req.AddHeader("Accept", "application/vnd.twitchtv.v5+json");

            var result = await client.ExecuteGetTaskAsync(req);
            dynamic responseObject = JObject.Parse(result.Content);

            return (string)responseObject.ingests[0].url_template;
        }

        #endregion

        #region ffMpeg


        /// <summary>
        /// Whether ffmpeg is running
        /// </summary>
        public bool IsFFMpegRunning
        {
            get
            {
                return Process.GetProcessesByName("ffmpeg").Length > 0;
            }
        }

        public bool IsFFMpegUsingQSV
        {
            get;
            set;
        }
        
        /// <summary>
        /// Kill all nginx processes
        /// </summary>
        private void FFMpegKill()
        {
            foreach (var process in Process.GetProcessesByName("ffmpeg"))
            {
                process.Kill();
            }
        }

        /// <summary>
        /// Start FFMpeg
        /// </summary>
        private async Task FFMpegStart()
        {
            this.FFMpegKill();

            if (string.IsNullOrEmpty(DotNetSettings.Default.FacebookStreamKey))
            {
                if (this.IsTestMode == false)
                {
                    return;
                }
            }

            if (Directory.Exists(Path.Combine(this.CurrentFolder, "ffmpeg-latest-win64-static")) == false)
            {
                await this.DownloadAndExtract("https://ffmpeg.zeranoe.com/builds/win64/static/ffmpeg-latest-win64-static.zip");
            }

            this.SetStatus("Starting ffmpeg", 0, true);

            // test for qsv suppport
            await Task.Run(() =>
            {
                var testFile = Path.Combine(this.CurrentFolder, "ffmpeg-latest-win64-static\\bin\\test.mp4");
                if (File.Exists(testFile) == false)
                {
                    File.Copy(Path.Combine(this.CurrentFolder, "Images\\test.mp4"), testFile);
                }

                var testOutFile = Path.Combine(this.CurrentFolder, "ffmpeg-latest-win64-static\\bin\\out.flv");
                if (File.Exists(testOutFile) == true)
                {
                    File.Delete(testOutFile);
                }


                string testTempate = "-i test.mp4 -vf scale=1280:-1 -sws_flags bilinear -vcodec h264_qsv -pix_fmt yuv420p -tune zerolatency -r 30 -g 60 -b:v %FBBITRATE%k -acodec aac -ar 44100 -threads 4 -q:a 3 -b:a 96K -bufsize 512k -f flv out.flv ";
                testTempate = testTempate.Replace("%FBBITRATE%", DotNetSettings.Default.FacebookBitRate.ToString());

                string exitmessage = null;

                ProcessStartInfo pi = new ProcessStartInfo();
                pi.FileName = Path.Combine(this.CurrentFolder, @"ffmpeg-latest-win64-static\bin\ffmpeg.exe");
                pi.Arguments = testTempate;
                pi.RedirectStandardError = true;
                pi.RedirectStandardOutput = false;
                pi.UseShellExecute = false;
                pi.CreateNoWindow = true;
                pi.WorkingDirectory = Path.Combine(this.CurrentFolder, @"ffmpeg-latest-win64-static\bin\");

                var process = Process.Start(pi);
                process.WaitForExit(10000);

                exitmessage = process.StandardError.ReadToEnd();

                if (exitmessage.Contains("Error initializing an internal MFX session: unsupported"))
                {
                    this.IsFFMpegUsingQSV = false;
                }
                else
                {
                    this.IsFFMpegUsingQSV = true;
                }
            });

            // real encoding
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(() =>
            {
                var runCommmand =
@"
@echo off

%CURRENTPATH%\ffmpeg-latest-win64-static\bin\ffmpeg -i ""rtmp://localhost/ingest/1"" -vf scale=1280:-1 -sws_flags bilinear -vcodec %VCODEC% -pix_fmt yuv420p -tune zerolatency -r 30 -g 60 -b:v %FBBITRATE%k -acodec aac -ar 44100 -threads 4 -q:a 3 -b:a 96K -bufsize 512k -f flv ""rtmp://localhost/tofacebook/1""

pause
";
                runCommmand = runCommmand.Replace("%CURRENTPATH%", this.CurrentFolder);
                runCommmand = runCommmand.Replace("%FBBITRATE%", DotNetSettings.Default.FacebookBitRate.ToString());
                runCommmand = runCommmand.Replace("%VCODEC%", this.IsFFMpegUsingQSV ? "h264_qsv" : "libx264");

                var batchFile = Path.Combine(this.CurrentFolder, "startffmpeg.cmd");
                File.WriteAllText(batchFile, runCommmand);

                tryagain:

                ProcessStartInfo pi = new ProcessStartInfo();
                pi.FileName = batchFile;
                pi.UseShellExecute = false;
                pi.WorkingDirectory = Path.Combine(this.CurrentFolder, @"ffmpeg-latest-win64-static\bin\");
                
                this.FFMpegKill();

                // real encoding
                var process = Process.Start(pi);
                bool exited = process.WaitForExit(120 * 1000); // monitor for 2 mins only

                if (exited)
                {
                    var result = MessageBox.Show(
                        "ffmpeg as exited unexpectedly. Click OK to restart ffmpeg.", "ffmpeg stopped.",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    
                    goto tryagain;
                }


            });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

            this.SetStatus();
            this.ListenToExit();
        }


        #endregion

        #region VLC
        
        /// <summary>
        /// Whether VLC is available
        /// </summary>
        public bool IsVLCAvailale
        {
            get
            {
                if (Directory.Exists(@"C:\Program Files (x86)\VideoLAN\VLC"))
                {
                    return true;
                }

                var vlcFolder = Path.Combine(this.CurrentFolder, "vlc -2.2.8");
                return Directory.Exists(vlcFolder);
            }
        }

        /// <summary>
        /// Whether vls is available
        /// </summary>
        public bool IsNeedToDownloadVLC
        {
            get
            {
                return this.IsVLCAvailale == false;
            }
        }
        
        private HashSet<VlcControl> _Init = new HashSet<VlcControl>();
        
        /// <summary>
        /// Start VLC Preview
        /// </summary>
        public void Stream(VlcControl control, string url)
        {
            if (this.IsVLCAvailale == false)
            {
                return;
            }
            
            var vlcFolder = Path.Combine(this.CurrentFolder, "vlc-2.2.8");
            if (_Init.Contains(control) == false)
            {
                control.MediaPlayer.VlcLibDirectoryNeeded += (s, e) =>
                {

                    if (Directory.Exists(@"C:\Program Files (x86)\VideoLAN\VLC"))
                    {
                        e.VlcLibDirectory = new DirectoryInfo(@"C:\Program Files (x86)\VideoLAN\VLC");
                    }
                    else
                    {
                        e.VlcLibDirectory = new DirectoryInfo(vlcFolder);
                    }

                };
                control.MediaPlayer.EndInit();
            }

            _Init.Add(control);
            control.MediaPlayer.Play(new Uri(url));
        }

        /// <summary>
        /// Download VLC
        /// </summary>
        public async void EnableVLC()
        {
            if (this.IsVLCAvailale)
            {
                return;
            }

            var vlcFolder = Path.Combine(this.CurrentFolder, "vlc-2.2.8");
            if (Directory.Exists(vlcFolder) == false)
            {
                await this.DownloadAndExtract("http://download.videolan.org/pub/videolan/vlc/last/win32/vlc-2.2.8-win32.zip");
            }

            this.OnPropertyChanged("IsVLCAvailale");
        }


        #endregion

        /// <summary>
        /// Start the process
        /// </summary>
        public async Task Start()
        {
            if (DotNetSettings.Default.IsStartOBS)
            {
                await this.StartOBS(this.OBSProfileSelected);
            }

            await this.NginxStart();

            await this.FFMpegStart();
        }

        private string CurrentFolder;
        private string OBSProfileFolder;

        private static ProjectData _Default;

        public static ProjectData Default
        {
            get
            {
                if (_Default == null)
                {
                    _Default = new ProjectData();

                    _Default.OBSProfileFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\basic\profiles");
                    _Default.CurrentFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    _Default.ListOBSProfiles();

                    _Default.Settings.Reload();
                    _Default.IsTestMode = true;
                }

                return _Default;
            }
        }

    }
}
