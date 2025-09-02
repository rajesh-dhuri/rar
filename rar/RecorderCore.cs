using Android.Content;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Provider;
using Java.IO;
using Java.Lang;   // for Integer
using System;
using System.Threading;
using System.Threading.Tasks;
using Uri = Android.Net.Uri;

namespace com.companyname.rar
{
    class RecorderCore
    {
        readonly Context _ctx;
        readonly string _treeUriString;
        readonly bool _useFront;

        CameraDevice _camera;
        CameraCaptureSession _session;
        MediaRecorder _recorder;
        HandlerThread _camThread;
        Handler _camHandler;

        public RecorderCore(Context ctx, string treeUriString, bool useFront)
        {
            _ctx = ctx;
            _treeUriString = treeUriString;
            _useFront = useFront;
        }

        string PickCameraId()
        {
            var mgr = (CameraManager)_ctx.GetSystemService(Context.CameraService);
            foreach (var id in mgr.GetCameraIdList())
            {
                var chars = mgr.GetCameraCharacteristics(id);
                var facingObj = (Integer)chars.Get(CameraCharacteristics.LensFacing);
                int? facing = facingObj != null ? (int?)facingObj.IntValue() : null;
                bool isFront = facing == (int)LensFacing.Front;
                if (_useFront == isFront)
                    return id;
            }
            var all = mgr.GetCameraIdList();
            return all.Length > 0 ? all[0] : null;
        }

        public async Task StartOneSegmentAsync(int seconds, CancellationToken ct)
        {
            string cameraId = PickCameraId();
            if (string.IsNullOrEmpty(cameraId))
                throw new InvalidOperationException("No camera available.");

            StartBackgroundThread();

            var mgr = (CameraManager)_ctx.GetSystemService(Context.CameraService);
            var tcsOpened = new TaskCompletionSource<bool>();

            mgr.OpenCamera(cameraId, new CameraStateCallback(
                onOpened: dev => { _camera = dev; tcsOpened.TrySetResult(true); },
                onDisconnected: dev => { dev.Close(); },
                onError: (dev, err) => { tcsOpened.TrySetException(new IOException("OpenCamera error: " + err)); }
            ), _camHandler);

            await tcsOpened.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Create SAF document: rar_yymmddhhmmss.rar  (mime: video/mp4)
            var fileName = "rar_" + DateTime.Now.ToString("yyMMddHHmmss") + ".rar";
            var tree = Uri.Parse(_treeUriString);
            var resolver = _ctx.ContentResolver;
            Uri docUri = DocumentsContract.CreateDocument(resolver, tree, "video/mp4", fileName);
            if (docUri == null) throw new IOException("Failed to create SAF document.");

            var pfd = resolver.OpenFileDescriptor(docUri, "w");
            if (pfd == null) throw new IOException("OpenFileDescriptor failed.");

            // Configure MediaRecorder (no preview surface)
            _recorder = new MediaRecorder();
            _recorder.SetAudioSource(AudioSource.Mic);
            _recorder.SetVideoSource(VideoSource.Surface);
            _recorder.SetOutputFormat(OutputFormat.Mpeg4);
            _recorder.SetAudioEncoder(AudioEncoder.Aac);
            _recorder.SetVideoEncoder(VideoEncoder.H264);
            _recorder.SetVideoEncodingBitRate(5_000_000);
            _recorder.SetVideoFrameRate(30);
            _recorder.SetVideoSize(1280, 720);
            _recorder.SetOutputFile(pfd.FileDescriptor);

            _recorder.Prepare();

            // Build session with only recorder surface
            var surface = _recorder.Surface;
            var tcsSession = new TaskCompletionSource<bool>();

            _camera.CreateCaptureSession(
                new System.Collections.Generic.List<Surface> { surface },
                new SessionStateCallback(
                    onConfigured: sess =>
                    {
                        _session = sess;
                        try
                        {
                            var builder = _camera.CreateCaptureRequest(CameraTemplate.Record);
                            builder.AddTarget(surface);
                            builder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
                            _session.SetRepeatingRequest(builder.Build(), null, _camHandler);
                            tcsSession.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcsSession.TrySetException(ex);
                        }
                    },
                    onConfigureFailed: sess => tcsSession.TrySetException(new IOException("ConfigureFailed"))
                ),
                _camHandler);

            await tcsSession.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            _recorder.Start();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { /* ignore */ }

            try { _recorder.Stop(); } catch { }
            _recorder.Reset();
            _recorder.Release();
            _recorder = null;

            pfd.Close();

            _session?.Close(); _session = null;
            _camera?.Close(); _camera = null;

            StopBackgroundThread();
        }

        public void StopAndRelease()
        {
            try { _session?.Close(); } catch { }
            try { _camera?.Close(); } catch { }
            try { _recorder?.Release(); } catch { }
            _session = null; _camera = null; _recorder = null;
            StopBackgroundThread();
        }

        void StartBackgroundThread()
        {
            _camThread = new HandlerThread("cam2");
            _camThread.Start();
            _camHandler = new Handler(_camThread.Looper);
        }

        void StopBackgroundThread()
        {
            if (_camThread != null)
            {
                _camThread.QuitSafely();
                try { _camThread.Join(); } catch { }
                _camThread = null;
                _camHandler = null;
            }
        }

        // small callbacks
        class CameraStateCallback : CameraDevice.StateCallback
        {
            readonly Action<CameraDevice> _onOpened;
            readonly Action<CameraDevice> _onDisconnected;
            readonly Action<CameraDevice, CameraError> _onError;
            public CameraStateCallback(Action<CameraDevice> onOpened, Action<CameraDevice> onDisconnected, Action<CameraDevice, CameraError> onError)
            { _onOpened = onOpened; _onDisconnected = onDisconnected; _onError = onError; }
            public override void OnOpened(CameraDevice camera) => _onOpened?.Invoke(camera);
            public override void OnDisconnected(CameraDevice camera) => _onDisconnected?.Invoke(camera);
            public override void OnError(CameraDevice camera, CameraError error) => _onError?.Invoke(camera, error);
        }

        class SessionStateCallback : CameraCaptureSession.StateCallback
        {
            readonly Action<CameraCaptureSession> _onConfigured;
            readonly Action<CameraCaptureSession> _onConfigureFailed;
            public SessionStateCallback(Action<CameraCaptureSession> onConfigured, Action<CameraCaptureSession> onConfigureFailed)
            { _onConfigured = onConfigured; _onConfigureFailed = onConfigureFailed; }
            public override void OnConfigured(CameraCaptureSession session) => _onConfigured?.Invoke(session);
            public override void OnConfigureFailed(CameraCaptureSession session) => _onConfigureFailed?.Invoke(session);
        }
    }
}
