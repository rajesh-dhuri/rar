using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using System;
using Uri = Android.Net.Uri;

namespace com.companyname.rar
{
    [Activity(Label = "rar", MainLauncher = true, Exported = true)]
    public class MainActivity : Activity
    {
        const int REQ_PERMS = 100;
        const int REQ_PICK_FOLDER = 101;

        TextView txtFolder, statusText;
        RadioButton radioRear, radioFront;
        EditText editSegmentSeconds;
        Button btnPickFolder, btnStart, btnStop;

        ISharedPreferences Prefs => GetSharedPreferences("app_settings", FileCreationMode.Private);

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_main);

            // Bind views (IDs must exist in activity_main.axml)
            txtFolder = FindViewById<TextView>(Resource.Id.txtFolder);
            statusText = FindViewById<TextView>(Resource.Id.statusText);
            radioRear = FindViewById<RadioButton>(Resource.Id.radioRear);
            radioFront = FindViewById<RadioButton>(Resource.Id.radioFront);
            editSegmentSeconds = FindViewById<EditText>(Resource.Id.editSegmentSeconds);
            btnPickFolder = FindViewById<Button>(Resource.Id.btnPickFolder);
            btnStart = FindViewById<Button>(Resource.Id.btnStart);
            btnStop = FindViewById<Button>(Resource.Id.btnStop);

            // Restore settings
            txtFolder.Text = Prefs.GetString("treeUri", "(none selected)");
            bool useFront = Prefs.GetBoolean("useFront", false);
            if (useFront) radioFront.Checked = true; else radioRear.Checked = true;
            editSegmentSeconds.Text = Prefs.GetInt("segment", 30).ToString();

            btnPickFolder.Click += (_, __) => PickFolder();
            btnStart.Click += (_, __) => StartSvc();
            btnStop.Click += (_, __) => StopSvc();

            RequestAllRuntimePermissions();
        }

        void RequestAllRuntimePermissions()
        {
            var perms = new[]
            {
                Manifest.Permission.Camera,
                Manifest.Permission.RecordAudio,
                Manifest.Permission.PostNotifications // Android 13+
            };

            bool need = false;
            foreach (var p in perms)
                if (ContextCompat.CheckSelfPermission(this, p) != Permission.Granted) need = true;

            if (need)
                ActivityCompat.RequestPermissions(this, perms, REQ_PERMS);
        }

        void PickFolder()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.PutExtra("android.content.extra.SHOW_ADVANCED", true);
            StartActivityForResult(intent, REQ_PICK_FOLDER);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode == REQ_PICK_FOLDER && resultCode == Result.Ok && data?.Data != null)
            {
                Uri tree = data.Data;
                var flags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                ContentResolver.TakePersistableUriPermission(tree, flags);

                Prefs.Edit().PutString("treeUri", tree.ToString()).Apply();
                txtFolder.Text = tree.ToString();
            }
        }

        void StartSvc()
        {
            // Save settings
            bool useFront = radioFront.Checked;
            int seg = 30;
            int.TryParse(editSegmentSeconds.Text?.Trim(), out seg);
            if (seg <= 0) seg = 30;

            Prefs.Edit().PutBoolean("useFront", useFront).PutInt("segment", seg).Apply();

            string tree = Prefs.GetString("treeUri", null);
            if (string.IsNullOrEmpty(tree))
            {
                Toast.MakeText(this, "Pick a folder first", ToastLength.Short).Show();
                return;
            }

            var svc = new Intent(this, typeof(VideoRecorderService));
            svc.PutExtra("treeUri", tree);
            svc.PutExtra("useFront", useFront);
            svc.PutExtra("segment", seg);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                StartForegroundService(svc);
            else
                StartService(svc);

            statusText.Text = "Service started";
        }

        void StopSvc()
        {
            StopService(new Intent(this, typeof(VideoRecorderService)));
            statusText.Text = "Service stopped";
        }
    }
}
