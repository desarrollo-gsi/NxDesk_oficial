using NxDesk.Application.Interfaces;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NxDesk.Infrastructure.Services
{
    public class NativeInputSimulator : IInputSimulator
    {
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; public static int Size => Marshal.SizeOf(typeof(INPUT)); }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        const int INPUT_MOUSE = 0; const int INPUT_KEYBOARD = 1;
        const uint MOUSEEVENTF_MOVE = 0x0001; const uint MOUSEEVENTF_ABSOLUTE = 0x8000; const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002; const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008; const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020; const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);


        public void MoveMouse(double x, double y, int screenIndex)
        {
            var screens = Screen.AllScreens;
            if (screenIndex >= screens.Length) screenIndex = 0;
            var bounds = screens[screenIndex].Bounds;

            int pixelX = bounds.X + (int)(x * bounds.Width);
            int pixelY = bounds.Y + (int)(y * bounds.Height);

            int vLeft = GetSystemMetrics(76);
            int vTop = GetSystemMetrics(77);
            int vWidth = GetSystemMetrics(78);
            int vHeight = GetSystemMetrics(79);

            int normX = (int)((pixelX - vLeft) * 65535.0 / vWidth);
            int normY = (int)((pixelY - vTop) * 65535.0 / vHeight);

            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi.dx = normX;
            input.u.mi.dy = normY;
            input.u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;

            SendInput(1, new[] { input }, INPUT.Size);
        }

        public void Click(string button, bool isDown)
        {
            uint flag = 0;
            switch (button.ToLower())
            {
                case "left": flag = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
                case "right": flag = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case "middle": flag = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            }
            if (flag != 0) SendMouseInput(flag);
        }

        public void Scroll(int delta)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi.dwFlags = MOUSEEVENTF_WHEEL;
            input.u.mi.mouseData = (uint)delta;
            SendInput(1, new[] { input }, INPUT.Size);
        }

        public void SendKey(string key, bool isDown)
        {
            if (Enum.TryParse<Keys>(key, true, out Keys winKey))
            {
                var input = new INPUT { type = INPUT_KEYBOARD };
                input.u.ki.wVk = (ushort)winKey;
                input.u.ki.dwFlags = isDown ? 0 : KEYEVENTF_KEYUP;
                SendInput(1, new[] { input }, INPUT.Size);
            }
        }

        private void SendMouseInput(uint flags)
        {
            var input = new INPUT { type = INPUT_MOUSE };
            input.u.mi.dwFlags = flags;
            SendInput(1, new[] { input }, INPUT.Size);
        }
    }
}