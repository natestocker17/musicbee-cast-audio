using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Sharpcaster.Models;

namespace MusicBeePlugin
{
    internal sealed class CastWindow : Form
    {
        private readonly ListBox devices = new ListBox();
        private readonly TextBox address = new TextBox();
        private readonly Label status = new Label();
        private List<ChromecastReceiver> found = new List<ChromecastReceiver>();

        internal event EventHandler RefreshClicked;
        internal event EventHandler CastClicked;
        internal event EventHandler StopClicked;

        internal CastWindow()
        {
            Text = "MusicBee Cast Audio";
            Size = new Size(420, 284);
            MinimumSize = new Size(420, 284);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            var label = new Label { Text = "Cast devices", Left = 16, Top = 14, Width = 250 };
            devices.SetBounds(16, 36, 286, 91);
            var refresh = Button("Refresh", 310, 36, 86, RefreshClickedHandler);
            var manual = new Label { Text = "Or enter Chromecast IPv4 address:", Left = 16, Top = 139, Width = 320 };
            address.SetBounds(16, 160, 206, 23);
            var cast = Button("Cast", 230, 159, 80, (s, e) => CastClicked?.Invoke(s, e));
            var stop = Button("Disconnect", 316, 159, 80, (s, e) => StopClicked?.Invoke(s, e));
            var hint = new Label { Text = "Use MusicBee's playback bar for play, seek, tracks and volume.", Left = 16, Top = 199, Width = 380 };
            status.SetBounds(16, 222, 380, 34);
            status.Text = "Select a device to cast the current MusicBee track.";
            Controls.AddRange(new Control[] { label, devices, refresh, manual, address, cast, stop, hint, status });
        }

        private void RefreshClickedHandler(object sender, EventArgs args) { RefreshClicked?.Invoke(sender, args); }
        private static Button Button(string text, int left, int top, int width, EventHandler handler)
        {
            var button = new Button { Text = text, Left = left, Top = top, Width = width, Height = 27 };
            button.Click += handler;
            return button;
        }

        internal string Status { set { if (!IsDisposed) status.Text = value; } }
        internal string ManualAddress { get { return address.Text.Trim(); } }
        internal ChromecastReceiver SelectedDevice
        {
            get { return devices.SelectedIndex >= 0 && devices.SelectedIndex < found.Count ? found[devices.SelectedIndex] : null; }
        }

        internal void SetDevices(List<ChromecastReceiver> receivers)
        {
            found = receivers.OrderBy(r => r.Name).ToList();
            devices.Items.Clear();
            foreach (var receiver in found) devices.Items.Add(receiver.Name + " (" + receiver.DeviceUri.Host + ")");
            if (found.Count > 0) devices.SelectedIndex = 0;
        }
    }

}
