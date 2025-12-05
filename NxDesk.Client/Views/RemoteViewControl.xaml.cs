using NxDesk.Application.DTOs;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace NxDesk.Client.Views
{
    public partial class RemoteViewControl : UserControl
    {
        public event Action<InputEvent> OnInputEvent;

        public RemoteViewControl()
        {
            InitializeComponent();

            VideoImage.MouseMove += (s, e) => SendMouse("mousemove", e);
            VideoImage.MouseDown += (s, e) => SendMouse("mousedown", e);
            VideoImage.MouseUp += (s, e) => SendMouse("mouseup", e);
            VideoImage.MouseWheel += (s, e) =>
            {
                OnInputEvent?.Invoke(new InputEvent { EventType = "mousewheel", Delta = e.Delta });
            };
        }

        public void SetFrame(BitmapSource frame)
        {
            VideoImage.Source = frame;
        }

        private void SendMouse(string type, MouseEventArgs e)
        {
            var pos = e.GetPosition(VideoImage);
            var button = "";

            if (e is MouseButtonEventArgs btnArgs)
            {
                button = btnArgs.ChangedButton.ToString().ToLower();
            }

            OnInputEvent?.Invoke(new InputEvent
            {
                EventType = type,
                X = pos.X / VideoImage.ActualWidth,
                Y = pos.Y / VideoImage.ActualHeight,
                Button = button
            });
        }
    }
}