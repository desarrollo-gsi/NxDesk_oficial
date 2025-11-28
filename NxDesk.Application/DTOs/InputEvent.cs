namespace NxDesk.Application.DTOs
{
    public class InputEvent
    {
        public string EventType { get; set; }

        public string? Key { get; set; }

        public double? X { get; set; }
        public double? Y { get; set; }
        public string? Button { get; set; }
        public double? Delta { get; set; }

        public string? Command { get; set; }

        public int? Value { get; set; }
    }
}
