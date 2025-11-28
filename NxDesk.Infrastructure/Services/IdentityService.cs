using NxDesk.Application.Interfaces;
using System.IO;

namespace NxDesk.Infrastructure.Services
{
    public class IdentityService : IIdentityService
    {
        private string _myId;
        private string _myAlias;
        private readonly string _configPath;
        private readonly string _idFilePath;

        public IdentityService()
        {
            _configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NxDesk");
            _idFilePath = Path.Combine(_configPath, "client.id");
            LoadOrCreateIdentity();
            _myAlias = Environment.MachineName;
        }

        public string GetMyId() => _myId;
        public string GetMyAlias() => _myAlias;

        private void LoadOrCreateIdentity()
        {
            if (!Directory.Exists(_configPath)) Directory.CreateDirectory(_configPath);

            if (File.Exists(_idFilePath))
            {
                _myId = File.ReadAllText(_idFilePath);
                if (string.IsNullOrWhiteSpace(_myId) || _myId.Length != 9)
                {
                    _myId = GenerateId();
                    File.WriteAllText(_idFilePath, _myId);
                }
            }
            else
            {
                _myId = GenerateId();
                File.WriteAllText(_idFilePath, _myId);
            }
        }

        private string GenerateId() => new Random().Next(100_000_000, 999_999_999).ToString();
    }
}