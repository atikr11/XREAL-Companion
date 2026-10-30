using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Unity.XR.XREAL.Experimental;
using WindowsProcess=System.Diagnostics.Process;
using ProcessStartInfo=System.Diagnostics.ProcessStartInfo;

namespace XREALCompanionStandalone
{
    public sealed class XREALCompanionApp : MonoBehaviour
    {
        const string BuildId="Companion HUD v2 / Direct USB";
        readonly ConcurrentQueue<DirectAir2Hid.Sample> samples=new ConcurrentQueue<DirectAir2Hid.Sample>();
        CancellationTokenSource stop;
        Thread reader;
        volatile bool connected;
        volatile string connection="Connecting to Air 2 USB sensors...";
        Transform head,panel;
        CompanionHud hud;
        Quaternion orientation=Quaternion.identity, reference=Quaternion.identity;
        Vector3 bias,sumGyro,sumAccel;
        ulong previous,calibrationStart;
        int calibrationSamples,processed;
        float stationarySeconds;
        bool calibrated,fullscreen,diagnostic,preview,help=false,placeAfterCalibration;
        bool settingsWindow;
        float lastSampleTime,startTime,nextPreferenceCheck,nextCommandCheck,nextAnswerCheck;
        long preferencesWriteTicks,answerWriteTicks;
        WindowsProcess settingsProcess;
        string status="Hold the glasses still for two seconds to calibrate.";

        void Start()
        {
            diagnostic=Array.IndexOf(Environment.GetCommandLineArgs(),"--diagnose-sensors")>=0;
            preview=Array.IndexOf(Environment.GetCommandLineArgs(),"--preview-hud")>=0;
            Application.runInBackground=true;
            Application.targetFrameRate=60;
            QualitySettings.vSyncCount=1;
            startTime=Time.realtimeSinceStartup;
            settingsWindow=Array.IndexOf(Environment.GetCommandLineArgs(),"--settings-window")>=0;
            if(settingsWindow) {
                Screen.SetResolution(1280,900,FullScreenMode.Windowed);
                head=new GameObject("Desktop Settings View").transform;
                CreatePanel(true);
                hud.PlaceHud=()=>SendControl("place|"+hud.Mode);
                hud.PlaceAllHud=()=>SendControl("place-all|"+hud.Mode);
                hud.Calibrate=()=>SendControl("calibrate");
                hud.PlaceWidgetAtGaze=(mode,id)=>SendControl("widget|"+mode+"|"+id);
                hud.AiAnswerChanged=PublishAiAnswer;
                ReadPreferenceTimestamp();
                StartCoroutine(PrepareSettingsWindow());
                Debug.Log("[Companion] Desktop settings window opened; sensor ownership remains with the main HUD.");
                return;
            }
            head=new GameObject("USB Tracked Head").transform;
            MakeEye("Left Eye",preview?0:-.032f,preview?new Rect(0,0,1,1):new Rect(0,0,.5f,1));
            if(!preview) MakeEye("Right Eye",.032f,new Rect(.5f,0,.5f,1));
            CreatePanel();
            if(preview) { Screen.SetResolution(1600,900,false);calibrated=true;PlacePanel();hud.SetTracking(true,true,0);StartCoroutine(CapturePreviews());return; }
            ReadPreferenceTimestamp();
            if(!diagnostic) ClearAnswerBridge();
            stop=new CancellationTokenSource();
            reader=new Thread(ReadSensors) { IsBackground=true, Name="Air2 USB IMU" };
            reader.Start();
            Debug.Log("[Companion] "+BuildId+" starting; vendor XR sensor runtime disabled.");
        }
        void MakeEye(string name,float x,Rect viewport)
        {
            var eye=new GameObject(name,typeof(Camera));
            eye.transform.SetParent(head,false);
            eye.transform.localPosition=new Vector3(x,0,0);
            var camera=eye.GetComponent<Camera>();
            camera.stereoTargetEye=StereoTargetEyeMask.None;
            camera.rect=viewport; camera.fieldOfView=25;
            camera.nearClipPlane=.05f; camera.farClipPlane=100;
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black;
            var data=eye.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.allowXRRendering=false;
        }
        void ReadSensors()
        {
            while(!stop.IsCancellationRequested) {
                try {
                    using(var device=new DirectAir2Hid(!diagnostic)) {
                        connection="Air 2 USB motion stream connected"; connected=true;
                        while(!stop.IsCancellationRequested) {
                            if(device.Read(stop.Token,out var sample)) {
                                if(samples.Count>2048) throw new InvalidOperationException("Sensor backlog exceeded two seconds.");
                                samples.Enqueue(sample);
                            }
                        }
                    }
                } catch(OperationCanceledException) { if(!stop.IsCancellationRequested) connection="USB sensor timeout; reconnecting..."; }
                catch(Exception e) { connection="USB sensor error: "+e.Message; }
                finally { connected=false; }
                if(stop.Token.WaitHandle.WaitOne(1000)) break;
            }
        }
        IEnumerator FillGlassesDisplay()
        {
            yield return new WaitForSecondsRealtime(1f);
            XREALExperimentalPlugin.SetWindowFullscreen(XREALExperimentalPlugin.MainWindowHandle);
            Screen.SetResolution(3840,1080,FullScreenMode.FullScreenWindow);
            yield return new WaitForSecondsRealtime(.5f);
            XREALExperimentalPlugin.SetWindowFullscreen(XREALExperimentalPlugin.MainWindowHandle);
        }
        void Update()
        {
            if(preview) return;
            SyncExternalPreferences();
            if(settingsWindow) { if(hud.WantsQuit) Application.Quit(); return; }
            panel=hud.Root;
            PollAiAnswer();
            PollControlCommands();
            hud.SetTracking(calibrated,connected,processed);
            if(hud.WantsQuit) Application.Quit();
            if(connected && !fullscreen && !diagnostic) { fullscreen=true; StartCoroutine(FillGlassesDisplay()); }
            int drained=0;
            while(drained++<2048 && samples.TryDequeue(out var s)) {
                lastSampleTime=Time.realtimeSinceStartup;
                Process(s); processed++;
            }
            if(calibrated && Time.realtimeSinceStartup-lastSampleTime>1f) {
                calibrated=false; previous=0; calibrationStart=0; calibrationSamples=0;
                stationarySeconds=0;
                sumGyro=sumAccel=Vector3.zero;
                hud.HideAllPanels();
                status="Motion stream lost. Waiting to reconnect and recalibrate.";
            }
            head.rotation=reference*orientation;
            var key=Keyboard.current;
            if(key!=null) {
                if(key.f1Key.wasPressedThisFrame) OpenSettingsWindow();
                if(key.escapeKey.wasPressedThisFrame) { if(hud.IsEditing) hud.ToggleEditor(); else Application.Quit(); }
                if(!hud.IsEditing) {
                if(key.tabKey.wasPressedThisFrame) help=!help;
                if(key.rKey.wasPressedThisFrame) ResetCalibration();
                if(key.pKey.wasPressedThisFrame && calibrated) {hud.PlaceAllPanelsAtGaze();panel=hud.Root;status="All panels moved together; their spacing is preserved.";}
                if(key.mKey.wasPressedThisFrame) hud.BeginPushToTalk();
                if(key.mKey.wasReleasedThisFrame) hud.EndPushToTalk();
                if(panel!=null && calibrated) {
                    Vector3 offset=Vector3.zero;
                    if(key.leftArrowKey.wasPressedThisFrame) offset-=panel.right*.1f;
                    if(key.rightArrowKey.wasPressedThisFrame) offset+=panel.right*.1f;
                    if(key.upArrowKey.wasPressedThisFrame) offset+=panel.up*.1f;
                    if(key.downArrowKey.wasPressedThisFrame) offset-=panel.up*.1f;
                    if(key.pageUpKey.wasPressedThisFrame) offset+=panel.forward*.15f;
                    if(key.pageDownKey.wasPressedThisFrame) offset-=panel.forward*.15f;
                    if(offset.sqrMagnitude>0) {panel.position+=offset;hud.CommitPanelPose(hud.Mode);}
                }
            }
            }
            if(diagnostic && Time.realtimeSinceStartup-startTime>8f) {
                bool pass=processed>1000 && calibrated;
                Debug.Log($"[Direct USB Diagnostic] pass={pass}; packets={processed}; calibrated={calibrated}; bias={bias}; orientation={orientation}; connection={connection}");
                Application.Quit(pass?0:2);
            }
        }
        void Process(DirectAir2Hid.Sample s)
        {
            Vector3 gyro=new Vector3(s.Gx,s.Gy,s.Gz), accel=new Vector3(s.Ax,s.Ay,s.Az);
            if(!Finite(gyro) || !Finite(accel)) return;
            if(previous!=0 && s.Timestamp<=previous) return;
            float dt=previous==0?0:(float)((s.Timestamp-previous)*1e-9);
            previous=s.Timestamp;
            if(!calibrated) {
                if(gyro.magnitude>5f || accel.magnitude<.8f || accel.magnitude>1.2f) {
                    calibrationStart=0; calibrationSamples=0; sumGyro=sumAccel=Vector3.zero;
                    stationarySeconds=0;
                    status="Hold the glasses still for two seconds to calibrate."; return;
                }
                if(calibrationStart==0) calibrationStart=s.Timestamp;
                sumGyro+=gyro; sumAccel+=accel; calibrationSamples++;
                double seconds=(s.Timestamp-calibrationStart)*1e-9;
                status="Calibrating sensors — hold still: "+Mathf.Clamp01((float)(seconds/2)).ToString("P0");
                if(seconds<2 || calibrationSamples<500) return;
                bias=sumGyro/calibrationSamples;
                stationarySeconds=0;
                orientation=Quaternion.FromToRotation((sumAccel/calibrationSamples).normalized,Vector3.up);
                reference=Quaternion.Inverse(Quaternion.Euler(0,orientation.eulerAngles.y,0));
                calibrated=true; head.rotation=reference*orientation;
                hud.InitializePanelAnchors();
                if(placeAfterCalibration) {hud.PlacePanelAtGaze(hud.Mode);placeAfterCalibration=false;}
                Debug.Log($"[Direct USB] Calibration complete; samples={calibrationSamples}; gyroBias={bias}; gravity={(sumAccel/calibrationSamples)}");
                return;
            }
            if(dt<=0 || dt>.1f) return;
            Vector3 correctedGyro=gyro-bias;
            // A MEMS gyro's zero-rate offset changes with temperature and time.
            // Refine it only after a sustained stationary period, following the
            // low-frequency bias estimator used by x-io Fusion (3 deg/s, 3 sec).
            if(Mathf.Abs(correctedGyro.x)>3f || Mathf.Abs(correctedGyro.y)>3f || Mathf.Abs(correctedGyro.z)>3f) stationarySeconds=0;
            else if(stationarySeconds<3f) stationarySeconds+=dt;
            else {
                float biasFilter=2f*Mathf.PI*.02f*dt;
                bias+=correctedGyro*biasFilter;
                correctedGyro=gyro-bias;
            }
            // Once stationary, discard the sub-threshold residual and sensor
            // noise that would otherwise integrate into visible yaw movement.
            if(stationarySeconds>=3f && correctedGyro.magnitude<.3f) correctedGyro=Vector3.zero;
            gyro=correctedGyro;
            float speed=gyro.magnitude;
            if(speed>.00001f) orientation=orientation*Quaternion.AngleAxis(speed*dt,gyro/speed);
            // Gravity correction removes pitch/roll drift without following the panel.
            if(accel.magnitude>.9f && accel.magnitude<1.1f) {
                Vector3 measuredUp=orientation*accel.normalized;
                Vector3 correction=Vector3.Cross(measuredUp,Vector3.up);
                float magnitude=correction.magnitude;
                if(magnitude>.000001f) orientation=Quaternion.AngleAxis(magnitude*.6f*dt*Mathf.Rad2Deg,correction/magnitude)*orientation;
            }
            orientation=Quaternion.Normalize(orientation);
        }
        static bool Finite(Vector3 v) => !float.IsNaN(v.sqrMagnitude) && !float.IsInfinity(v.sqrMagnitude);
        void ResetCalibration()
        {
            placeAfterCalibration=true;
            calibrated=false; calibrationStart=0; calibrationSamples=0; previous=0;
            stationarySeconds=0;
            sumGyro=sumAccel=Vector3.zero;
            if(panel!=null) panel.gameObject.SetActive(false);
            status="Hold the glasses still for two seconds to calibrate.";
        }
        void PlacePanel()
        {
            if(panel==null) CreatePanel();
            hud.InitializePanelAnchors();hud.PlacePanelAtGaze(hud.Mode);panel=hud.Root;
            status="Calibrated. Panels are independently anchored. P places the selected panel again.";
        }
        void CreatePanel()
        {
            hud=gameObject.AddComponent<CompanionHud>();
            hud.Configure(head,preview||diagnostic,false);
            hud.Calibrate=ResetCalibration;
            hud.PlaceHud=PlacePanel;
            hud.PlaceWidgetAtGaze=(mode,id)=>SendControl("widget|"+mode+"|"+id);
            panel=hud.Root;
        }
        void CreatePanel(bool desktopWindow)
        {
            hud=gameObject.AddComponent<CompanionHud>();
            hud.Configure(head,false,desktopWindow);
            panel=hud.Root;
        }
        IEnumerator PrepareSettingsWindow()
        {
            yield return new WaitForSecondsRealtime(.8f);
            WindowsProcess current=WindowsProcess.GetCurrentProcess();
            if(current.MainWindowHandle!=IntPtr.Zero) {
                SetWindowText(current.MainWindowHandle,"XREAL Companion // Desktop Settings");
                SetWindowPos(current.MainWindowHandle,IntPtr.Zero,80,60,1280,900,0x0040);
            }
        }
        void OpenSettingsWindow()
        {
            try {
                if(settingsProcess!=null && !settingsProcess.HasExited) {
                    if(settingsProcess.MainWindowHandle!=IntPtr.Zero) SetForegroundWindow(settingsProcess.MainWindowHandle);
                    return;
                }
                string executable=WindowsProcess.GetCurrentProcess().MainModule.FileName;
                settingsProcess=WindowsProcess.Start(new ProcessStartInfo { FileName=executable,Arguments="--settings-window",UseShellExecute=true });
            } catch(Exception e) { Debug.LogError("[Companion] Could not open desktop settings: "+e.Message); }
        }
        string ControlDirectory => Path.Combine(Application.persistentDataPath,"control-commands");
        void SendControl(string command)
        {
            try {
                Directory.CreateDirectory(ControlDirectory);
                string file=Path.Combine(ControlDirectory,"control-"+Guid.NewGuid().ToString("N")+".cmd");
                File.WriteAllText(file,command);
            } catch(Exception e) { Debug.LogError("[Companion] Could not send a command to the glasses window: "+e.Message); }
        }
        void PollControlCommands()
        {
            if(Time.unscaledTime<nextCommandCheck) return;
            nextCommandCheck=Time.unscaledTime+.2f;
            try {
                if(!Directory.Exists(ControlDirectory)) return;
                foreach(string file in Directory.GetFiles(ControlDirectory,"control-*.cmd")) {
                    string command=File.ReadAllText(file);File.Delete(file);
                    if(command=="place" && calibrated) PlacePanel();
                    else if(command.StartsWith("place|") && calibrated) {
                        if(int.TryParse(command.Substring(6),out int mode)) {hud.SetMode(mode);PlacePanel();}
                    }
                    else if(command.StartsWith("place-all|") && calibrated) {
                        if(int.TryParse(command.Substring(10),out int mode)) {hud.SetMode(mode);hud.PlaceAllPanelsAtGaze();panel=hud.Root;status="All panels moved together and saved.";}
                    }
                    else if(command=="calibrate") ResetCalibration();
                    else if(command.StartsWith("widget|")) {
                        string[] pieces=command.Split('|');
                if(pieces.Length==3 && int.TryParse(pieces[1],out int mode)) hud.PlaceWidgetAtGazeNow(mode,pieces[2]);
                    }
                }
            } catch(Exception e) { Debug.LogWarning("[Companion] Settings command could not be applied: "+e.Message); }
        }
        string PreferencesPath => Path.Combine(Application.persistentDataPath,"companion-hud.json");
        void ReadPreferenceTimestamp() { try { preferencesWriteTicks=File.Exists(PreferencesPath)?File.GetLastWriteTimeUtc(PreferencesPath).Ticks:0; } catch {} }
        void SyncExternalPreferences()
        {
            if(hud==null || hud.HasPendingChanges || Time.unscaledTime<nextPreferenceCheck) return;
            nextPreferenceCheck=Time.unscaledTime+.5f;
            try {
                long ticks=File.Exists(PreferencesPath)?File.GetLastWriteTimeUtc(PreferencesPath).Ticks:0;
                if(ticks!=0 && ticks!=preferencesWriteTicks) { preferencesWriteTicks=ticks;hud.ReloadExternalChanges(); }
            } catch(Exception e) { Debug.LogWarning("[Companion] Could not sync desktop settings: "+e.Message); }
        }
        string AiBridgePath => Path.Combine(Application.persistentDataPath,"companion-ai-session.txt");
        void ClearAnswerBridge() { try { if(File.Exists(AiBridgePath)) File.Delete(AiBridgePath);answerWriteTicks=0; } catch {} }
        void PublishAiAnswer(string value)
        {
            try {
                string temp=AiBridgePath+".tmp";File.WriteAllText(temp,value??"");
                if(File.Exists(AiBridgePath)) File.Replace(temp,AiBridgePath,null); else File.Move(temp,AiBridgePath);
            } catch(Exception e) { Debug.LogWarning("[Companion] Could not share the AI answer with the HUD: "+e.Message); }
        }
        void PollAiAnswer()
        {
            if(Time.unscaledTime<nextAnswerCheck) return;nextAnswerCheck=Time.unscaledTime+.5f;
            try {
                if(!File.Exists(AiBridgePath)) return;
                long ticks=File.GetLastWriteTimeUtc(AiBridgePath).Ticks;
                if(ticks!=answerWriteTicks) { answerWriteTicks=ticks;hud.SetExternalAnswer(File.ReadAllText(AiBridgePath)); }
            } catch(Exception e) { Debug.LogWarning("[Companion] Could not read the AI answer: "+e.Message); }
        }
        void OnApplicationQuit() { if(!settingsWindow) ClearAnswerBridge(); }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool SetWindowText(IntPtr window,string text);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr insertAfter,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        IEnumerator CapturePreviews()
        {
            yield return new WaitForSecondsRealtime(1f);
            yield return hud.VerifyPreviewFeatures();
            string[] modes={"Daily","Study","Developer","AI"};
            for(int i=0;i<modes.Length;i++) {
                hud.SetMode(i);
                hud.SetPreviewPanels(i);
                yield return new WaitForSecondsRealtime(.5f);
                yield return new WaitForEndOfFrame();
                string path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath),"HUD-Preview-"+modes[i]+".png");
                CaptureWorldPreview(path);
                yield return new WaitForSecondsRealtime(.3f);
                Debug.Log("[HUD Preview] "+path);
            }
            Application.Quit();
        }
        void CaptureWorldPreview(string path)
        {
            var camera=head.GetComponentInChildren<Camera>();
            var target=new RenderTexture(1600,900,24);
            target.Create();
            Canvas.ForceUpdateCanvases();
            var renderRequest=new UnityEngine.Rendering.Universal.UniversalRenderPipeline.SingleCameraRequest { destination=target };
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera,renderRequest);
            var previousTarget=RenderTexture.active;
            RenderTexture.active=target;
            var capture=new Texture2D(1600,900,TextureFormat.RGB24,false);
            capture.ReadPixels(new Rect(0,0,1600,900),0,0);capture.Apply();
            System.IO.File.WriteAllBytes(path,capture.EncodeToPNG());
            RenderTexture.active=previousTarget;
            Destroy(capture);target.Release();Destroy(target);
            Debug.Log($"[HUD Capture] active={panel.gameObject.activeSelf}; position={panel.position}; scale={panel.localScale}");
        }
        void OnGUI()
        {
            if(diagnostic || preview || (hud!=null && hud.IsEditing) || (!help && calibrated)) return;
            float eyeWidth=Screen.width*.5f;
            for(int eye=0;eye<2;eye++) {
                GUI.BeginGroup(new Rect(eye*eyeWidth,0,eyeWidth,Screen.height));
                GUI.Box(new Rect(16,16,Mathf.Min(700,eyeWidth-32),140),BuildId);
                GUI.Label(new Rect(28,44,eyeWidth-56,24),connection);
                GUI.Label(new Rect(28,68,eyeWidth-56,24),status);
                GUI.Label(new Rect(28,92,eyeWidth-56,24),$"Packets {processed} | Head {head.eulerAngles} | F1 desktop settings | R calibrate | P place | Tab hide | Esc quit");
                GUI.EndGroup();
            }
        }
        void OnDestroy()
        {
            if(settingsProcess!=null) settingsProcess.Dispose();
            if(stop!=null) {
                stop.Cancel();
                bool finished=reader==null || !reader.IsAlive || reader.Join(2500);
                if(finished) stop.Dispose();
            }
        }
        void OnApplicationFocus(bool focused) { if(!focused && hud!=null)hud.EndPushToTalk(); }
    }
}

