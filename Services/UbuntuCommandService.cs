using Renci.SshNet;
using System;
using System.Threading.Tasks;

namespace GCS_Project.Services
{
    public class UbuntuCommandService
    {
        private readonly string _host;
        private readonly string _username;
        private readonly string _password;

        public UbuntuCommandService(string host, string username, string password)
        {
            _host = host;
            _username = username;
            _password = password;
        }

        // Ubuntu의 tmux PX4 콘솔에 MAVLink 연결 명령을 입력한다.
        public Task StartMavlinkToGcsAsync(string gcsIp)
        {
            string command =
                $"tmux send-keys -t px4 \"mavlink start -u 14551 -o 14550 -t {gcsIp}\" Enter";

            return RunCommandAsync(command);
        }

        private Task RunCommandAsync(string command)
        {
            return Task.Run(() =>
            {
                using var client = new SshClient(_host, _username, _password);

                client.Connect();

                if (!client.IsConnected)
                    throw new InvalidOperationException("Ubuntu SSH 연결에 실패했습니다.");

                var result = client.RunCommand(command);

                if (!string.IsNullOrWhiteSpace(result.Error))
                    throw new InvalidOperationException(result.Error);

                client.Disconnect();
            });
        }
    }
}
