namespace NxDesk.Application.Interfaces
{
    public interface IInputSimulator
    {
        void MoveMouse(double x, double y, int screenIndex);
        void Click(string button, bool isDown);
        void Scroll(int delta);
        void SendKey(string key, bool isDown);
    }
}