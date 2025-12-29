using NxDesk.Application.DTOs;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace NxDesk.Client.Views
{
    public partial class RemoteViewControl : UserControl
    {
        public event Action<InputEvent> OnInputEvent;
        
        // Throttling para eventos de mouse (reduce carga de red)
        private readonly Stopwatch _mouseMoveThrottle = Stopwatch.StartNew();
        private const int MOUSE_THROTTLE_MS = 16; // ~60 FPS máximo para eventos de mouse

        public RemoteViewControl()
        {
            InitializeComponent();

            // Permitir que el control reciba foco para capturar teclado
            this.Focusable = true;
            this.Loaded += (s, e) => this.Focus();
            this.MouseDown += (s, e) => this.Focus(); // Click para enfocar

            // Eventos de teclado
            this.KeyDown += (s, e) =>
            {
                OnInputEvent?.Invoke(new InputEvent { EventType = "keydown", Key = e.Key.ToString() });
                e.Handled = true;
            };
            this.KeyUp += (s, e) =>
            {
                OnInputEvent?.Invoke(new InputEvent { EventType = "keyup", Key = e.Key.ToString() });
                e.Handled = true;
            };

            VideoImage.MouseMove += (s, e) => 
            {
                // Throttle: solo enviar si han pasado al menos MOUSE_THROTTLE_MS
                if (_mouseMoveThrottle.ElapsedMilliseconds >= MOUSE_THROTTLE_MS)
                {
                    SendMouse("mousemove", e);
                    _mouseMoveThrottle.Restart();
                }
            };
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

        public void ClearFrame()
        {
            VideoImage.Source = null;
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