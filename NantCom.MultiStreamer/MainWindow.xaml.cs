using NantCom.MultiStreamer.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using MahApps.Metro.Controls;
using System.Windows.Threading;

namespace NantCom.MultiStreamer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : MetroWindow
    {
        DispatcherTimer tmr = new DispatcherTimer();

        public MainWindow()
        {
            InitializeComponent();

            VisualStateManager.GoToElementState(this.Content as Grid, "NginxOff", false);
            VisualStateManager.GoToElementState(this.Content as Grid, "QSVOff", false);
            VisualStateManager.GoToElementState(this.Content as Grid, "FFMpegOff", false);

            tmr.Interval = TimeSpan.FromSeconds(5);
            tmr.Tick += (s, e) =>
            {
                this.RefreshStatus();
            };
        }

        private void Image_Click(object sender, MouseButtonEventArgs e)
        {
            Process.Start((sender as FrameworkElement).Tag.ToString());
        }
        private void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            Process.Start((sender as Hyperlink).TargetName);
        }

        private void RefreshStatus()
        {
            if (ProjectData.Default.IsNginXRunning)
            {
                VisualStateManager.GoToElementState(this.Content as Grid, "NginxOn", false);
            }
            else
            {
                VisualStateManager.GoToElementState(this.Content as Grid, "NginxOff", false);
            }

            if (ProjectData.Default.IsFFMpegRunning)
            {
                VisualStateManager.GoToElementState(this.Content as Grid, "FFMpegOn", false);
            }
            else
            {
                VisualStateManager.GoToElementState(this.Content as Grid, "FFMpegOff", false);
            }

            if (ProjectData.Default.IsFFMpegUsingQSV)
            {
                VisualStateManager.GoToElementState(this.Content as Grid, "QSVOn", false);
            }
            else
            {
                VisualStateManager.GoToElementState(this.Content as Grid, "QSVOff", false);
            }

            try
            {
                this.RTMPStat.Navigate("http://localhost:8080/stat");
            }
            catch (Exception)
            {
            }
        }

        private async void StartStreamer_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectData.Default.IsObsRunning)
            {
                MessageBox.Show("OBS is running, please close it first", "Sorry!", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            this.IsEnabled = false;

            VisualStateManager.GoToElementState(this.Content as Grid, "NginxStarting", false);
            VisualStateManager.GoToElementState(this.Content as Grid, "QSVStarting", false);
            VisualStateManager.GoToElementState(this.Content as Grid, "FFMpegStarting", false);

            try
            {

                await ProjectData.Default.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Oops!", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            this.IsEnabled = true;

            tmr.Start();

        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            Properties.Settings.Default.Save();
            MessageBox.Show("Settings are saved", "Done!", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Monitoring_Click(object sender, RoutedEventArgs e)
        {
            ProjectData.Default.EnableVLC();

            ProjectData.Default.Stream(this.VLCFacebook, "rtmp://localhost/tofacebook/1");
            ProjectData.Default.Stream(this.VLCMain, "rtmp://localhost/ingest/1");
        }

        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender != this.MainTab)
            {
                return;
            }

            if (this.VLCFacebook == null)
            {
                return;
            }

            if (this.MainTab.SelectedIndex == 3)
            {
                this.RTMPStat.Navigate("http://localhost:8080/stat");
            }

            if (ProjectData.Default.IsVLCAvailale == true)
            {
                if (this.MainTab.SelectedIndex == 3)
                {
                    ProjectData.Default.Stream(this.VLCFacebook, "rtmp://localhost/tofacebook/1");
                    ProjectData.Default.Stream(this.VLCMain, "rtmp://localhost/ingest/1");
                }
                else
                {
                    this.VLCFacebook.MediaPlayer.Stop();
                    this.VLCMain.MediaPlayer.Stop();
                }
            }

        }

        private void RTMPStat_Navigated(object sender, NavigationEventArgs e)
        {
            var document = this.RTMPStat.Document as mshtml.HTMLDocument;

            if (document != null)
            {
                var head = document.getElementsByTagName("head").OfType<mshtml.HTMLHeadElement>().FirstOrDefault();

                if (head != null)
                {
                    var styleSheet = (mshtml.IHTMLStyleSheet)document.createStyleSheet("", 0);
                    styleSheet.cssText =
@"
body {
    overflow-x: hidden;
}

* {
    font-family: 'Segoe UI';
    font-size: 7pt;
}
";
                }
                
            }

        }

    }
}
