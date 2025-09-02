using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace com.companyname.rar
{
    [Service(Exported = false,
        ForegroundServiceType = Android.Content.PM.ForegroundService.TypeCamera | Android.Content.PM.ForegroundService.TypeMicrophone)]
    public class VideoRecorderService : Service
    {
        const int NOTIF_ID = 7;
        const string CHANNEL_ID = "rec_channel";

        RecorderCore _core;
        CancellationTokenSource _cts;

        public override void OnCreate()
        {
            base.OnCreate();
            CreateNotificationChannel();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            var treeUri = intent.GetStringExtra("treeUri");
            bool useFront = intent.GetBooleanExtra("useFront", false);
            int segment = intent.GetIntExtra("segment", 30);
            if (segment <= 0) segment = 30;

            var notif = BuildNotification("Recording…");
            StartForeground(NOTIF_ID, notif);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _core = new RecorderCore(this, treeUri, useFront);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        await _core.StartOneSegmentAsync(segment, _cts.Token);
                        // loop: each call records exactly 'segment' seconds and closes the file (rar_yymmddhhmmss.rar)
                    }
                }
                catch { }
                finally
                {
                    _core?.StopAndRelease();
                    StopForeground(true);
                    StopSelf();
                }
            }, _cts.Token);

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            try { _cts?.Cancel(); } catch { }
            _core?.StopAndRelease();
            base.OnDestroy();
        }

        public override IBinder OnBind(Intent intent) => null;

        void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var nm = (NotificationManager)GetSystemService(NotificationService);
                var channel = new NotificationChannel(CHANNEL_ID, "Recording", NotificationImportance.Low)
                {
                    Description = "Foreground recording service"
                };
                nm.CreateNotificationChannel(channel);
            }
        }

        Notification BuildNotification(string text)
        {
            var builder = new NotificationCompat.Builder(this, CHANNEL_ID)
                .SetContentTitle("rar")
                .SetContentText(text)
                .SetSmallIcon(Resource.Mipmap.ic_gear) // use your gear mipmap
                .SetOngoing(true);

            return builder.Build();
        }
    }
}
