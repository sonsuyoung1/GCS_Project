using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public class GuidanceCommandUdpService
    {
        private readonly string _ubuntuIp;
        private readonly int _ubuntuPort;

        public GuidanceCommandUdpService(string ubuntuIp, int ubuntuPort = 6001)
        {
            _ubuntuIp = ubuntuIp;
            _ubuntuPort = ubuntuPort;
        }

        public Task SendSelectedTargetAsync(int targetId)
        {
            var payload = new
            {
                command = "SELECT_TARGET",
                target_id = targetId
            };

            return SendJsonAsync(payload);
        }

        public Task SendStartGuidanceAsync()
        {
            var payload = new
            {
                command = "START_GUIDANCE"
            };

            return SendJsonAsync(payload);
        }

        public Task SendStopGuidanceAsync()
        {
            var payload = new
            {
                command = "STOP_GUIDANCE"
            };

            return SendJsonAsync(payload);
        }

        private async Task SendJsonAsync(object payload)
        {
            string json = JsonSerializer.Serialize(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using var udp = new UdpClient();
            await udp.SendAsync(bytes, bytes.Length, _ubuntuIp, _ubuntuPort);
        }
    }
}