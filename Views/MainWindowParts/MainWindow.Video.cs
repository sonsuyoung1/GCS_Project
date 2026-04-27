using LibVLCSharp.Shared;
using System;
using System.IO;
using System.Windows;

namespace GCS_Project
{
    public partial class MainWindow
    {
        #region [영상 초기화]

        // VLC 비디오 플레이어를 초기화하고 VideoView에 연결한다.
        private void InitializeVideoPlayer()
        {
            Core.Initialize();

            _libVLC = new LibVLC("--network-caching=0");
            _mp1 = new MediaPlayer(_libVLC);

            VideoView1.MediaPlayer = _mp1;
        }

        #endregion


        #region [영상 재생]

        // 실행 폴더에 있는 로컬 영상 파일을 재생한다.
        private void PlayLocalFile(MediaPlayer? player, string fileName)
        {
            if (player == null || _libVLC == null) return;

            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            if (!File.Exists(path))
            {
                MessageBox.Show($"파일 없음: {path}\n실행 폴더(bin\\...)에 {fileName}이 있어야 함");
                return;
            }

            using var media = new Media(_libVLC, new Uri(path));
            player.Play(media);
        }

        // SDP 파일을 이용해 RTP 영상 스트림을 재생한다.
        private void PlayRtpStream(MediaPlayer? player)
        {
            if (player == null || _libVLC == null) return;

            string sdpPath = @"C:\Users\82106\OneDrive\바탕 화면\stream.sdp";

            if (!File.Exists(sdpPath))
            {
                MessageBox.Show($"SDP 파일 없음: {sdpPath}");
                return;
            }

            using var media = new Media(_libVLC, new Uri(sdpPath));
            player.Play(media);
        }

        #endregion
    }
}