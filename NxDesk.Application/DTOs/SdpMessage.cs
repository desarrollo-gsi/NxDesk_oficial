namespace NxDesk.Application.DTOs
{
    public class SdpMessage
    {
        public string Type { get; set; }
        public string Payload { get; set; }
        public string? SenderId { get; set; }
    }
}
