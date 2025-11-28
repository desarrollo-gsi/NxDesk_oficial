namespace NxDesk.Domain.Entities
{
    public class DiscoveredDevice
    {
        public string ConnectionID { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
    }
}