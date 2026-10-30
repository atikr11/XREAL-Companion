using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Vosk;

namespace XREALCompanionStandalone
{
    // Presentation and local productivity state. Tracking remains in XREALCompanionApp.
    public sealed class CompanionHud : MonoBehaviour
    {
        [Serializable] public class TaskItem { public string title; public bool done; }
        [Serializable] public class Flashcard { public string question,answer; }
        [Serializable] public class Placement { public string id; public float x,y,scale=1; public bool visible=true; }
        [Serializable] public class PanelPlacement { public int mode; public float x,y,z,rx,ry,rz,rw=1,scale=1; public bool visible=true,anchored; }
        [Serializable] public class VoiceCommand { public string phrase; public int action,target=1; public bool enabled=true; }
        [Serializable] public class Profile { public List<Placement> widgets=new List<Placement>(); }
        [Serializable] public class Preferences {
            public int version=2, mode,layoutPreset=1; public bool timerRunning,showPanelHints=true; public float brightness=.85f,opacity=.86f,textScale=1;
            public string notes="",developerNotes="",endpoint="https://api.openai.com/v1/chat/completions",model="";
            public List<TaskItem> tasks=new List<TaskItem>(); public List<Flashcard> cards=new List<Flashcard>();
            public List<Profile> profiles=new List<Profile>(); public List<PanelPlacement> panels=new List<PanelPlacement>(); public List<VoiceCommand> voiceCommands=new List<VoiceCommand>(); public string voiceDevice=""; public float timerSeconds=1500; public int timerMode;
        }
        sealed class PanelView { public int mode; public RectTransform root; public Text header,footer; public Image shell; }
        sealed class Widget { public string id; public int mode; public RectTransform root; public Text title,body; public float width,height; }
        [Serializable] class Message { public string role,content; }
        [Serializable] class ChatRequest { public string model; public Message[] messages; public int max_tokens=350; public float temperature=.3f; }
        [Serializable] class ChatChoice { public Message message; }
        [Serializable] class ChatResponse { public ChatChoice[] choices; }
        [Serializable] class VoiceResult { public string text; }
        static readonly string[] Modes={"DAILY","SCHOOL","DEVELOPER","AI"};
        readonly List<Widget> widgets=new List<Widget>(); readonly List<PanelView> panelViews=new List<PanelView>();
        readonly Color green=new Color(.35f,1f,.62f),muted=new Color(.48f,.72f,.58f);
        Preferences state=new Preferences(); Transform head; Font font;
        public Transform Root => panelViews.Count>state.mode?panelViews[state.mode].root:null;
        public int Mode => state.mode;
        public bool IsEditing {get; private set;}
        public bool WantsQuit {get; private set;}
        public bool Preview {get; private set;}
        public bool HasPendingChanges => dirty;
        public Action Calibrate;
        public Action PlaceHud;
        public Action PlaceAllHud;
        public Action<int,string> PlaceWidgetAtGaze;
        public Action<string> AiAnswerChanged;
        bool timerRunning,back,dirty,tracking,usb,layoutEditor,desktopSettingsWindow;
        int settingsPage;
        bool voiceEditorOpen,voiceListening; volatile bool voiceStopRequested;
        bool voicePushToTalk;
        Model voiceModel; VoskRecognizer voiceRecognizer; Thread voiceWorker; AudioClip microphoneClip; string microphoneName=""; int microphoneReadPosition;
        readonly ConcurrentQueue<short[]> voiceAudio=new ConcurrentQueue<short[]>(); readonly ConcurrentQueue<string> voiceResults=new ConcurrentQueue<string>(); readonly ConcurrentQueue<string> voiceErrors=new ConcurrentQueue<string>();
        float nextPaint,nextSystem,saveAt,toastUntil; int packets,cardIndex,selected,answerPage;
        string path,toast="",taskDraft="",cardQuestion="",cardAnswer="",apiKey="",prompt="",answer="",aiStatus="Configure a provider in F1 to ask a question.",saveError="";
        string voicePhraseDraft="check task 1",voiceTargetDraft="1",voiceStatus="Voice commands are off.",lastVoiceTranscript=""; int voiceActionDraft;
        string systemText="Reading system status..."; UnityWebRequest request; Vector2 scroll;
        GUIStyle labelStyle,titleStyle,buttonStyle,fieldStyle,boxStyle;
        Texture2D guiPanelTexture,guiButtonTexture,guiButtonHoverTexture,guiFieldTexture;
        public void Configure(Transform trackedHead,bool preview,bool settingsWindow=false)
        {
            head=trackedHead; Preview=preview; desktopSettingsWindow=settingsWindow;
            path=Path.Combine(Application.persistentDataPath,"companion-hud.json");
            if(!preview) Load();
            timerRunning=state.timerRunning;
            Sanitize();
            try { font=Font.CreateDynamicFontFromOSFont("Consolas",28); } catch {}
            if(font==null) font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            for(int mode=0;mode<4;mode++) {
                var root=new GameObject("Companion // "+Modes[mode]+" Panel",typeof(RectTransform),typeof(Canvas));
                root.transform.SetParent(transform,false);
                var rect=(RectTransform)root.transform;rect.sizeDelta=new Vector2(1000,580);rect.localScale=Vector3.one*PresetScale();
                root.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
                var view=new PanelView { mode=mode,root=rect };
                var shell=new GameObject("HUD glass",typeof(RectTransform),typeof(Image));shell.transform.SetParent(rect,false);
                var shellRect=(RectTransform)shell.transform;shellRect.sizeDelta=new Vector2(1000,580);
                view.shell=shell.GetComponent<Image>();view.shell.color=new Color(.008f,.025f,.018f,.94f);view.shell.raycastTarget=false;
                Box(rect,new Vector2(0,287),new Vector2(1000,3),new Color(.2f,1f,.55f,.95f));
                Box(rect,new Vector2(-496,0),new Vector2(3,574),new Color(.2f,1f,.55f,.62f));
                Box(rect,new Vector2(496,0),new Vector2(3,574),new Color(.2f,1f,.55f,.62f));
                Box(rect,new Vector2(0,-287),new Vector2(1000,3),new Color(.2f,1f,.55f,.62f));
                view.header=MakeText("COMPANION / "+Modes[mode],rect,new Vector2(0,269),new Vector2(980,32),22,green);
                view.footer=MakeText("",rect,new Vector2(0,-269),new Vector2(980,28),16,muted);
                panelViews.Add(view); ApplyPanelPose(view);
            }
            Rebuild();SetPanelVisibility();
            if(settingsWindow) IsEditing=true;
        }
        void Sanitize()
        {
            if(state.version<2){state.showPanelHints=true;state.version=2;}
            state.layoutPreset=Mathf.Clamp(state.layoutPreset,0,2);state.mode=Mathf.Clamp(state.mode,0,3); state.brightness=Mathf.Clamp(state.brightness,.25f,1);
            state.opacity=Mathf.Clamp(state.opacity,.35f,1); state.textScale=Mathf.Clamp(state.textScale,.8f,1.25f);
            state.notes=Limit(state.notes??"",8000); state.developerNotes=Limit(state.developerNotes??"",8000); state.tasks=state.tasks??new List<TaskItem>(); state.cards=state.cards??new List<Flashcard>();
            state.tasks.RemoveAll(t=>t==null || string.IsNullOrWhiteSpace(t.title));
            state.cards.RemoveAll(c=>c==null || string.IsNullOrWhiteSpace(c.question));
            if(state.tasks.Count>30) state.tasks.RemoveRange(30,state.tasks.Count-30);
            if(state.cards.Count>100) state.cards.RemoveRange(100,state.cards.Count-100);
            state.voiceCommands=state.voiceCommands??new List<VoiceCommand>();
            state.voiceCommands.RemoveAll(c=>c==null || string.IsNullOrWhiteSpace(c.phrase) || c.action<0 || c.action>4);
            if(state.voiceCommands.Count==0) {
                state.voiceCommands.Add(new VoiceCommand {phrase="check task 1",action=0,target=1});
                state.voiceCommands.Add(new VoiceCommand {phrase="complete task 1",action=0,target=1});
            }
            if(state.voiceCommands.Count>50)state.voiceCommands.RemoveRange(50,state.voiceCommands.Count-50);
            state.voiceDevice=state.voiceDevice??"";
            state.profiles=state.profiles??new List<Profile>();
            while(state.profiles.Count<4) state.profiles.Add(new Profile());
            for(int i=0;i<4;i++) { if(state.profiles[i]==null) state.profiles[i]=new Profile(); if(state.profiles[i].widgets==null) state.profiles[i].widgets=new List<Placement>(); }
            state.panels=state.panels??new List<PanelPlacement>();
            for(int i=0;i<4;i++) {
                var placement=state.panels.Find(p=>p!=null && p.mode==i);
                if(placement==null) {
                    var offset=i==0?new Vector3(0,0,2):i==1?new Vector3(-1.35f,0,2):i==2?new Vector3(1.35f,0,2):new Vector3(0,1.05f,2);
                    placement=new PanelPlacement {mode=i,x=offset.x,y=offset.y,z=offset.z,visible=true};state.panels.Add(placement);
                }
                placement.scale=Mathf.Clamp(Finite(placement.scale)?placement.scale:1,.65f,1.25f);
                if(!Finite(placement.x))placement.x=0;if(!Finite(placement.y))placement.y=0;if(!Finite(placement.z))placement.z=2;
                if(!Finite(placement.rx))placement.rx=0;if(!Finite(placement.ry))placement.ry=0;if(!Finite(placement.rz))placement.rz=0;if(!Finite(placement.rw))placement.rw=1;
            }
            if(float.IsNaN(state.timerSeconds) || float.IsInfinity(state.timerSeconds)) state.timerSeconds=1500;
            state.timerSeconds=Mathf.Clamp(state.timerSeconds,0,86400); state.timerMode=Mathf.Clamp(state.timerMode,0,1);
            state.endpoint=state.endpoint??""; state.model=state.model??"";
        }
        void Load()
        {
            try { if(File.Exists(path)) state=JsonUtility.FromJson<Preferences>(File.ReadAllText(path))??new Preferences(); }
            catch(Exception e) { saveError="Could not load saved preferences: "+e.Message; state=new Preferences(); }
        }
        void MarkDirty() { dirty=true; saveAt=Time.unscaledTime+1; }
        void Save()
        {
            if(Preview) return;
            try {
                state.timerRunning=timerRunning;
                string temp=path+".tmp"; File.WriteAllText(temp,JsonUtility.ToJson(state,true),Encoding.UTF8);
                if(File.Exists(path)) File.Replace(temp,path,path+".bak"); else File.Move(temp,path);
                dirty=false; saveError="";
            } catch(Exception e) { saveError="Save failed: "+e.Message; dirty=false; }
        }
        public void ReloadExternalChanges()
        {
            if(Preview || dirty || !File.Exists(path)) return;
            try {
                state=JsonUtility.FromJson<Preferences>(File.ReadAllText(path))??new Preferences();
                Sanitize();timerRunning=state.timerRunning; selected=0; answerPage=0;
                foreach(var view in panelViews) ApplyPanelPose(view);
                Rebuild();SetPanelVisibility();
            } catch(Exception e) { saveError="Could not refresh settings: "+e.Message; }
        }
        public void SetMode(int mode) { state.mode=Mathf.Clamp(mode,0,3); selected=0; answerPage=0; MarkDirty(); }
        void Rebuild()
        {
            foreach(var widget in widgets) Destroy(widget.root.gameObject); widgets.Clear();
            for(int mode=0;mode<4;mode++) {
                switch(mode) {
                    case 0: Add(mode,"clock","01 / LOCAL TIME",-254,119,472,222); Add(mode,"tasks","02 / TASK QUEUE",254,3,472,454); Add(mode,"timer","03 / FOCUS SESSION",-254,-119,472,218); break;
                    case 1: Add(mode,"notes","01 / STUDY NOTES",-169,3,642,454); Add(mode,"cards","02 / RECALL",339,119,302,222); Add(mode,"timer","03 / FOCUS",339,-119,302,218); break;
                    case 2: Add(mode,"system","01 / SYSTEM MONITOR",-254,119,472,222); Add(mode,"notes","02 / DEVELOPER NOTES",254,3,472,454); Add(mode,"tasks","03 / TASK QUEUE",-254,-119,472,218); break;
                    case 3: Add(mode,"ai","01 / ASSISTANT",0,3,980,454); break;
                }
            }
            Paint();
        }
        void Add(int mode,string id,string title,float x,float y,float width,float height)
        {
            Placement p=state.profiles[mode].widgets.Find(w=>w!=null && w.id==id);
            if(p==null) { p=new Placement { id=id,x=x,y=y }; state.profiles[mode].widgets.Add(p); }
            var card=new GameObject(title,typeof(RectTransform),typeof(Image)); card.transform.SetParent(panelViews[mode].root,false);
            var rect=(RectTransform)card.transform; rect.sizeDelta=new Vector2(width,height);
            var widget=new Widget { id=id,mode=mode,root=rect,width=width,height=height }; widgets.Add(widget);
            card.GetComponent<Image>().color=new Color(.005f,.025f,.014f,state.opacity);
            Box(rect,new Vector2(0,height*.5f-1),new Vector2(width,2),green*.65f);
            Box(rect,new Vector2(-width*.5f+1,0),new Vector2(2,height),green*.25f);
            Box(rect,new Vector2(0,-height*.5f+1),new Vector2(width,1),green*.25f);
            widget.title=MakeText(title,rect,new Vector2(-width*.5f+16,height*.5f-25),new Vector2(width-32,28),17,muted,TextAnchor.MiddleLeft,true);
            widget.body=MakeText("",rect,new Vector2(-width*.5f+16,-12),new Vector2(width-32,height-74),24,green,TextAnchor.UpperLeft,true);
            widget.body.verticalOverflow=VerticalWrapMode.Truncate;
            Apply(widget,p);
        }
        void Apply(Widget w,Placement p)
        {
            if(float.IsNaN(p.scale)||float.IsInfinity(p.scale)) p.scale=1;
            p.scale=Mathf.Clamp(p.scale,.65f,Mathf.Min(1.25f,1000f/w.width,476f/w.height));
            if(float.IsNaN(p.x)||float.IsInfinity(p.x)) p.x=0;
            if(float.IsNaN(p.y)||float.IsInfinity(p.y)) p.y=0;
            p.x=Mathf.Clamp(p.x,-Mathf.Max(0,500-w.width*p.scale*.5f),Mathf.Max(0,500-w.width*p.scale*.5f));
            p.y=Mathf.Clamp(p.y,-Mathf.Max(0,238-w.height*p.scale*.5f),Mathf.Max(0,238-w.height*p.scale*.5f));
            w.root.anchoredPosition=new Vector2(p.x,p.y); w.root.localScale=Vector3.one*p.scale; w.root.gameObject.SetActive(p.visible);
        }
        void Box(Transform parent,Vector2 pos,Vector2 size,Color color)
        {
            var go=new GameObject("Rule",typeof(RectTransform),typeof(Image)); go.transform.SetParent(parent,false);
            var r=(RectTransform)go.transform; r.anchoredPosition=pos; r.sizeDelta=size; go.GetComponent<Image>().color=color;
        }
        Text MakeText(string value,Transform parent,Vector2 position,Vector2 size,int fontSize,Color color,TextAnchor alignment=TextAnchor.MiddleLeft,bool left=false)
        {
            var go=new GameObject(value,typeof(RectTransform),typeof(Text)); go.transform.SetParent(parent,false);
            var r=(RectTransform)go.transform; r.anchoredPosition=position; r.sizeDelta=size;
            if(left) r.pivot=new Vector2(0,.5f);
            var t=go.GetComponent<Text>(); t.font=font; t.fontSize=fontSize; t.color=color; t.alignment=alignment; t.text=value;
            t.supportRichText=false; t.horizontalOverflow=HorizontalWrapMode.Wrap; return t;
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        PanelPlacement PanelState(int mode) => state.panels.Find(p=>p!=null && p.mode==mode);
        void ApplyPanelPose(PanelView view)
        {
            var p=PanelState(view.mode);if(p==null)return;
            view.root.position=new Vector3(p.x,p.y,p.z);
            var rotation=new Quaternion(p.rx,p.ry,p.rz,p.rw);
            float rotationMagnitude=rotation.x*rotation.x+rotation.y*rotation.y+rotation.z*rotation.z+rotation.w*rotation.w;
            view.root.rotation=rotationMagnitude<.0001f?Quaternion.identity:Quaternion.Normalize(rotation);
            view.root.localScale=Vector3.one*PresetScale()*p.scale;
        }
        void SetPanelVisibility()
        {
            foreach(var view in panelViews) { var p=PanelState(view.mode);if(p!=null)view.root.gameObject.SetActive(p.visible && (tracking || Preview)); }
        }
        public void SetTracking(bool valid,bool connected,int packetCount)
        {
            bool changed=tracking!=valid;tracking=valid;usb=connected;packets=packetCount;
            if(changed) { if(valid)SetPanelVisibility();else HideAllPanels(); }
        }
        public void HideAllPanels() { foreach(var view in panelViews)view.root.gameObject.SetActive(false); }
        public void Place() => PlacePanelAtGaze(state.mode);
        public void PlacePanelAtGaze(int mode)
        {
            if(mode<0 || mode>=panelViews.Count || head==null)return;
            state.mode=mode;
            var view=panelViews[mode];view.root.SetPositionAndRotation(head.position+head.forward*2f,head.rotation);
            SavePanelPose(mode,view);SetPanelVisibility();MarkDirty();
        }
        public void PlaceAllPanelsAtGaze()
        {
            if(head==null || panelViews.Count==0)return;
            var anchor=panelViews[state.mode].root;
            Vector3 oldCenter=Vector3.zero;foreach(var view in panelViews)oldCenter+=view.root.position;
            oldCenter/=panelViews.Count;
            Vector3 newPosition=head.position+head.forward*2f;
            Quaternion rotationDelta=head.rotation*Quaternion.Inverse(anchor.rotation);
            foreach(var view in panelViews) {
                view.root.position=newPosition+rotationDelta*(view.root.position-oldCenter);
                view.root.rotation=rotationDelta*view.root.rotation;
                SavePanelPose(view.mode,view);
            }
            SetPanelVisibility();MarkDirty();
        }
        void SavePanelPose(int mode,PanelView view)
        {
            var p=PanelState(mode);if(p==null)return;
            p.x=view.root.position.x;p.y=view.root.position.y;p.z=view.root.position.z;
            p.rx=view.root.rotation.x;p.ry=view.root.rotation.y;p.rz=view.root.rotation.z;p.rw=view.root.rotation.w;p.anchored=true;
        }
        public void CommitPanelPose(int mode)
        {
            if(mode<0 || mode>=panelViews.Count)return;
            SavePanelPose(mode,panelViews[mode]);MarkDirty();
        }
        public void InitializePanelAnchors()
        {
            for(int mode=0;mode<panelViews.Count;mode++) {
                var p=PanelState(mode);if(p==null || p.anchored)continue;
                Vector3 offset=mode==0?new Vector3(0,0,2):mode==1?new Vector3(-1.35f,0,2):mode==2?new Vector3(1.35f,0,2):new Vector3(0,1.05f,2);
                var view=panelViews[mode];view.root.SetPositionAndRotation(head.position+head.rotation*offset,head.rotation);SavePanelPose(mode,view);
            }
            SetPanelVisibility();MarkDirty();
        }
        public void SetPanelVisible(int mode,bool visible)
        {
            var p=PanelState(mode);if(p==null)return;p.visible=visible;SetPanelVisibility();MarkDirty();
        }
        public bool PanelVisible(int mode) { var p=PanelState(mode);return p!=null && p.visible; }
        public void SetPreviewPanels(int visibleMode)
        {
            foreach(var view in panelViews)view.root.gameObject.SetActive(view.mode==visibleMode);
        }
        public void ToggleEditor() { IsEditing=!IsEditing; if(!IsEditing) { GUIUtility.keyboardControl=0; Save(); } }
        public void Notify(string message) { toast=message; toastUntil=Time.unscaledTime+8; }
        void Update()
        {
            if(Root==null) return;
            if(timerRunning) {
                state.timerSeconds=Mathf.Max(0,state.timerSeconds-Time.unscaledDeltaTime);
                if(state.timerSeconds<=0) { timerRunning=false;state.timerRunning=false; MarkDirty(); Notify(state.timerMode==0?"FOCUS COMPLETE // take a break":"BREAK COMPLETE // ready to focus"); }
            }
            if(dirty && Time.unscaledTime>=saveAt) Save();
            if(Time.unscaledTime>=nextSystem) { nextSystem=Time.unscaledTime+1; systemText=SystemSnapshot(); }
            UpdateVoiceInput();
            if(Time.unscaledTime>=nextPaint) { nextPaint=Time.unscaledTime+.2f; Paint(); }
            var k=Keyboard.current;
            if(k==null) return;
            if(desktopSettingsWindow && GUIUtility.keyboardControl==0) {
                if(k.mKey.wasPressedThisFrame) BeginPushToTalk();
                if(k.mKey.wasReleasedThisFrame) EndPushToTalk();
            }
            if(IsEditing) return;
            if(k.altKey.isPressed) {
                if(k.digit1Key.wasPressedThisFrame) SetMode(0); if(k.digit2Key.wasPressedThisFrame) SetMode(1);
                if(k.digit3Key.wasPressedThisFrame) SetMode(2); if(k.digit4Key.wasPressedThisFrame) SetMode(3);
            }
            if(k.spaceKey.wasPressedThisFrame) { if(state.mode==1) back=!back; else ToggleTimer(); }
            if(k.nKey.wasPressedThisFrame && state.mode==1) NextCard(1);
            if(k.hKey.wasPressedThisFrame && Root!=null) Root.gameObject.SetActive(!Root.gameObject.activeSelf);
        }
        string VoiceModelPath => Path.Combine(Application.streamingAssetsPath,"Vosk","vosk-model-small-en-us-0.15");
        static readonly string[] VoiceActions={"Complete task","Start / pause timer","Show panel","Hide panel","Select panel"};
        void StartVoiceListening()
        {
            try {
                if(voiceWorker!=null && voiceWorker.IsAlive) {voiceStatus="Finishing the previous phrase; wait a moment.";return;}
                if(Microphone.devices.Length==0) {voiceStatus="No microphone is available to Windows.";return;}
                if(voiceModel==null) {
                    if(!Directory.Exists(VoiceModelPath)) {voiceStatus="Offline English model is missing from the app folder.";return;}
                    voiceStatus="Loading the offline English model...";Vosk.Vosk.SetLogLevel(-1);voiceModel=new Model(VoiceModelPath);
                }
                voiceRecognizer=new VoskRecognizer(voiceModel,16000f,BuildVoiceGrammar());
                voiceRecognizer.SetMaxAlternatives(0);
                microphoneName=Array.IndexOf(Microphone.devices,state.voiceDevice)>=0?state.voiceDevice:null;
                microphoneClip=Microphone.Start(microphoneName,true,10,16000);
                if(microphoneClip==null) {voiceRecognizer.Dispose();voiceRecognizer=null;voiceStatus="Windows could not start the selected microphone.";return;}
                microphoneReadPosition=Microphone.GetPosition(microphoneName);if(microphoneReadPosition<0)microphoneReadPosition=0;
                while(voiceAudio.TryDequeue(out _)){} while(voiceResults.TryDequeue(out _)){} while(voiceErrors.TryDequeue(out _)){}
                voiceStopRequested=false;voiceListening=true;
                voiceWorker=new Thread(VoiceRecognitionLoop){IsBackground=true,Name="Offline voice commands"};voiceWorker.Start();
                voiceStatus="Listening locally. Say a saved phrase, then pause.";
            } catch(Exception e) { voiceListening=false;voiceStatus="Voice setup failed: "+e.Message;Debug.LogWarning("[Voice] "+e); }
        }
        string BuildVoiceGrammar()
        {
            var json=new StringBuilder("[");bool first=true;
            foreach(var command in state.voiceCommands) if(command!=null && command.enabled && !string.IsNullOrWhiteSpace(command.phrase)) {
                if(!first)json.Append(',');first=false;json.Append('"').Append(JsonString(GrammarPhrase(command.phrase))).Append('"');
            }
            if(!first)json.Append(',');json.Append("\"[unk]\"]");return json.ToString();
        }
        static string JsonString(string value) => (value??"").Replace("\\","\\\\").Replace("\"","\\\"");
        static string GrammarPhrase(string value)
        {
            string[] words=value.ToLowerInvariant().Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
            string[] numberWords={"zero","one","two","three","four","five","six","seven","eight","nine","ten"};
            for(int i=0;i<words.Length;i++) if(int.TryParse(words[i],out int number) && number>=0 && number<numberWords.Length)words[i]=numberWords[number];
            return string.Join(" ",words);
        }
        void UpdateVoiceInput()
        {
            if(voiceListening && microphoneClip!=null) {
                int write=Microphone.GetPosition(microphoneName);if(write>=0 && write!=microphoneReadPosition) {
                    int count=write>=microphoneReadPosition?write-microphoneReadPosition:microphoneClip.samples-microphoneReadPosition+write;
                    if(count>0 && count<=microphoneClip.samples) {
                        float[] captured=new float[count];int first=Math.Min(count,microphoneClip.samples-microphoneReadPosition);
                        if(first>0){var part=new float[first];microphoneClip.GetData(part,microphoneReadPosition);Array.Copy(part,0,captured,0,first);}
                        if(count>first){var part=new float[count-first];microphoneClip.GetData(part,0);Array.Copy(part,0,captured,first,part.Length);}
                        microphoneReadPosition=write;QueueResampledAudio(captured,microphoneClip.frequency);
                    }
                }
            }
            while(voiceResults.TryDequeue(out string result)) HandleVoiceTranscript(result);
            while(voiceErrors.TryDequeue(out string error)) voiceStatus="Voice recognition error: "+error;
            if(!voiceListening && voiceWorker!=null && !voiceWorker.IsAlive) {voiceWorker=null;if(voiceStatus.StartsWith("Stopping"))voiceStatus="Microphone is off.";}
        }
        void QueueResampledAudio(float[] captured,int sourceRate)
        {
            if(sourceRate<=0 || captured.Length==0)return;
            int count=Mathf.Max(1,Mathf.RoundToInt(captured.Length*(16000f/sourceRate)));var converted=new short[count];
            for(int i=0;i<count;i++) {
                float position=i*(sourceRate/16000f);int left=Mathf.Min((int)position,captured.Length-1),right=Mathf.Min(left+1,captured.Length-1);
                float value=Mathf.Lerp(captured[left],captured[right],position-left);
                converted[i]=(short)Mathf.RoundToInt(Mathf.Clamp(value,-1f,1f)*32767f);
            }
            voiceAudio.Enqueue(converted);
        }
        void VoiceRecognitionLoop()
        {
            var recognizer=voiceRecognizer;
            try {
                while(!voiceStopRequested || !voiceAudio.IsEmpty) {
                    if(voiceAudio.TryDequeue(out short[] audio)) { if(recognizer.AcceptWaveform(audio,audio.Length))QueueRecognizedJson(recognizer.Result()); }
                    else Thread.Sleep(2);
                }
                QueueRecognizedJson(recognizer.FinalResult());
            } catch(Exception e) {voiceErrors.Enqueue(e.Message);}
            finally {if(recognizer!=null)recognizer.Dispose();voiceRecognizer=null;}
        }
        void QueueRecognizedJson(string json)
        {
            try {var parsed=JsonUtility.FromJson<VoiceResult>(json);if(!string.IsNullOrWhiteSpace(parsed?.text))voiceResults.Enqueue(parsed.text.Trim());}
            catch(Exception e) {voiceErrors.Enqueue(e.Message);}
        }
        static string NormalizeCommand(string phrase)
        {
            phrase=(phrase??"").ToLowerInvariant();
            string[] words={"zero","one","two","three","four","five","six","seven","eight","nine","ten"};
            for(int i=0;i<words.Length;i++)phrase=Regex.Replace(phrase,@"\b"+words[i]+@"\b",i.ToString());
            return Regex.Replace(Regex.Replace(phrase,@"[^a-z0-9 ]"," "),@"\s+"," ").Trim();
        }
        void HandleVoiceTranscript(string transcript)
        {
            lastVoiceTranscript=transcript;string normalized=NormalizeCommand(transcript);
            var command=state.voiceCommands.Find(c=>c!=null && c.enabled && NormalizeCommand(c.phrase)==normalized);
            if(command==null) {voiceStatus="Heard: \""+transcript+"\" — no saved phrase matched.";return;}
            switch(command.action) {
                case 0:
                    if(command.target<1 || command.target>state.tasks.Count) {voiceStatus="Heard the command, but task "+command.target+" does not exist.";return;}
                    state.tasks[command.target-1].done=true;MarkDirty();Notify("VOICE // checked task "+command.target);Paint();voiceStatus="Completed task "+command.target+".";break;
                case 1:ToggleTimer();voiceStatus=timerRunning?"Focus timer started.":"Timer paused.";break;
                case 2:case 3:
                    if(command.target<1 || command.target>4){voiceStatus="Choose a panel number from 1 to 4.";return;}
                    SetPanelVisible(command.target-1,command.action==2);voiceStatus=(command.action==2?"Showing ":"Hiding ")+Modes[command.target-1]+".";break;
                case 4:
                    if(command.target<1 || command.target>4){voiceStatus="Choose a section number from 1 to 4.";return;}
                    SetMode(command.target-1);voiceStatus="Selected "+Modes[command.target-1]+".";break;
            }
        }
        void StopVoiceListening()
        {
            voiceListening=false;voiceStopRequested=true;
            if(microphoneClip!=null){Microphone.End(microphoneName);microphoneClip=null;}
            voiceStatus="Stopping microphone and finishing the phrase...";
        }
        public void BeginPushToTalk()
        {
            voicePushToTalk=true;
            if(!voiceListening)StartVoiceListening();
            voiceStatus=voiceListening?"M held // listening locally...":voiceStatus;
        }
        public void EndPushToTalk()
        {
            if(!voicePushToTalk)return;
            voicePushToTalk=false;
            if(voiceListening)StopVoiceListening();
        }
        void Paint()
        {
            if(panelViews.Count==0) return;
            string footerText=Time.unscaledTime<toastUntil?toast:"F1 desktop settings    P place all    M hold to talk    Alt+1..4 select    R calibrate    H hide    Esc exit";
            foreach(var view in panelViews) {
                view.header.text="XREAL COMPANION     /     "+Modes[view.mode]+"                                      "+(tracking?"● ONLINE":"◌ CALIBRATING");
                view.header.color=green*state.brightness;view.footer.text=footerText;view.footer.color=muted*state.brightness;
                view.footer.gameObject.SetActive(state.showPanelHints);
                if(view.shell!=null)view.shell.color=new Color(.008f,.025f,.018f,Mathf.Clamp01(.76f+state.opacity*.24f));
            }
            foreach(var w in widgets) {
                w.body.fontSize=Mathf.RoundToInt((w.id=="clock"?40:w.id=="timer"?32:23)*state.textScale);
                w.body.color=green*state.brightness; w.title.color=muted*state.brightness;
                w.root.GetComponent<Image>().color=new Color(.005f,.025f,.014f,state.opacity);
                switch(w.id) {
                    case "clock": w.body.text=DateTime.Now.ToString("HH:mm:ss")+"\n"+DateTime.Now.ToString("ddd, dd MMM yyyy").ToUpperInvariant(); break;
                    case "timer": w.body.text=TimeText(state.timerSeconds)+"\n"+(state.timerMode==0?"FOCUS":"BREAK")+" / "+(timerRunning?"RUNNING":"PAUSED")+(w.mode==1?"\n[F1] timer controls":"\n[SPACE] toggle"); break;
                    case "tasks": w.body.text=TaskText(w.height>300?9:3); break;
                    case "notes": string note=w.mode==2?state.developerNotes:state.notes; w.body.text=string.IsNullOrWhiteSpace(note)?"Your workspace is clear.\n\n[F1] Add notes for this session.":Limit(note,1100); break;
                    case "cards": w.body.text=state.cards.Count==0?"No flashcards yet.\n\n[F1] Add a question.":$"{cardIndex+1:D2} / {state.cards.Count:D2}  {(back?"ANSWER":"QUESTION")}\n\n"+Limit(back?state.cards[cardIndex].answer:state.cards[cardIndex].question,300)+"\n\n[SPACE] flip   [N] next"; break;
                    case "system": w.body.text=systemText; break;
                    case "ai": int pages=Mathf.Max(1,Mathf.CeilToInt(answer.Length/800f)); answerPage=Mathf.Clamp(answerPage,0,pages-1); w.body.text=string.IsNullOrEmpty(answer)?"A concise answer, in your field of view.\n\n"+aiStatus+"\n\n[F1] Set provider, model, and question.":$"RESPONSE / {answerPage+1} OF {pages}\n\n"+answer.Substring(answerPage*800,Mathf.Min(800,answer.Length-answerPage*800)); break;
                }
            }
            // Dynamic OS fonts can repack their atlas when a profile requests
            // new glyphs/sizes. Refresh persistent labels as well as new cards.
            foreach(var view in panelViews) {
                var labels=view.root.GetComponentsInChildren<Text>(true);
                foreach(var label in labels) font.RequestCharactersInTexture(label.text,label.fontSize,label.fontStyle);
                foreach(var label in labels) label.SetAllDirty();
            }
        }
        string TaskText(int max)
        {
            var s=new StringBuilder(); int completed=state.tasks.FindAll(t=>t.done).Count;
            s.AppendLine($"{state.tasks.Count-completed:D2} OPEN / {completed:D2} DONE\n");
            if(state.tasks.Count==0) s.Append("[F1] Add your first task.");
            int count=0; foreach(var task in state.tasks) { if(count++>=max) { s.Append("... more in F1"); break; } s.AppendLine((task.done?"[x] ":"[ ] ")+Limit(task.title,48)); }
            return s.ToString();
        }
        static string TimeText(float seconds) { int total=Mathf.CeilToInt(seconds); return $"{total/60:00}:{total%60:00}"; }
        static string Limit(string text,int size) => text==null?"":text.Length>size?text.Substring(0,size):text;
        void ToggleTimer() { if(state.timerSeconds<=0) state.timerSeconds=state.timerMode==0?1500:300; timerRunning=!timerRunning;state.timerRunning=timerRunning; MarkDirty(); }
        void NextCard(int delta) { if(state.cards.Count>0) cardIndex=(cardIndex+delta+state.cards.Count)%state.cards.Count; back=false; Paint(); }
        void Styles()
        {
            if(labelStyle!=null) return;
            guiPanelTexture=GuiTexture(new Color(.025f,.055f,.04f,1));
            guiButtonTexture=GuiTexture(new Color(.055f,.115f,.08f,1));
            guiButtonHoverTexture=GuiTexture(new Color(.08f,.24f,.14f,1));
            guiFieldTexture=GuiTexture(new Color(.018f,.035f,.027f,1));
            labelStyle=new GUIStyle(GUI.skin.label) { font=font,fontSize=19,wordWrap=true }; labelStyle.normal.textColor=green;
            titleStyle=new GUIStyle(labelStyle) { fontSize=25,fontStyle=FontStyle.Bold };
            buttonStyle=new GUIStyle(GUI.skin.button) { font=font,fontSize=17,padding=new RectOffset(12,12,10,10),wordWrap=true };
            buttonStyle.normal.background=guiButtonTexture;buttonStyle.hover.background=guiButtonHoverTexture;buttonStyle.active.background=guiButtonHoverTexture;
            buttonStyle.normal.textColor=green;buttonStyle.hover.textColor=Color.white;buttonStyle.active.textColor=Color.white;
            fieldStyle=new GUIStyle(GUI.skin.textField) { font=font,fontSize=19,wordWrap=true }; fieldStyle.normal.textColor=green;fieldStyle.focused.textColor=Color.white;fieldStyle.normal.background=guiFieldTexture;
            boxStyle=new GUIStyle(GUI.skin.box) { padding=new RectOffset(22,22,18,18) };boxStyle.normal.textColor=green;boxStyle.normal.background=guiPanelTexture;
        }
        Texture2D GuiTexture(Color color)
        {
            var texture=new Texture2D(1,1,TextureFormat.RGBA32,false);texture.SetPixel(0,0,color);texture.Apply();texture.hideFlags=HideFlags.HideAndDontSave;return texture;
        }
        void OnGUI()
        {
            if(!IsEditing || Root==null) return; Styles();
            if(desktopSettingsWindow) { DrawDesktopSettings(); return; }
            int eyes=Preview||desktopSettingsWindow?1:2; float eyeWidth=Screen.width/(float)eyes;
            for(int i=0;i<eyes;i++) {
                GUI.BeginGroup(new Rect(i*eyeWidth,0,eyeWidth,Screen.height));
                float width=Mathf.Min(870,eyeWidth-60),height=Mathf.Min(900,Screen.height-60);
                GUI.backgroundColor=new Color(.035f,.12f,.065f,.98f); GUI.contentColor=green;
                GUILayout.BeginArea(new Rect((eyeWidth-width)*.5f,30,width,height),boxStyle);
                GUILayout.Label(desktopSettingsWindow?"COMPANION // DESKTOP SETTINGS":"COMPANION // CONTROL",titleStyle);
                GUILayout.BeginHorizontal(); for(int mode=0;mode<4;mode++) if(GUILayout.Button((state.mode==mode?"> ":"")+Modes[mode],buttonStyle)) SetMode(mode); GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal(); GUILayout.Label("VISIBLE PANELS",labelStyle,GUILayout.Width(155));
                for(int mode=0;mode<4;mode++) { bool visible=GUILayout.Toggle(PanelVisible(mode),Modes[mode],labelStyle);if(visible!=PanelVisible(mode))SetPanelVisible(mode,visible); }
                GUILayout.EndHorizontal();
                GUILayout.Space(8); scroll=GUILayout.BeginScrollView(scroll);
                if(state.mode==0) { TimerEditor(); TaskEditor(); }
                if(state.mode==1) { NotesEditor(); FlashcardEditor(); TimerEditor(); }
                if(state.mode==2) { GUILayout.Label(systemText,labelStyle); NotesEditor(); TaskEditor(); }
                if(state.mode==3) AiEditor();
                if(GUILayout.Button(voiceEditorOpen?"CLOSE VOICE COMMANDS":"VOICE COMMANDS // OFFLINE",buttonStyle)) voiceEditorOpen=!voiceEditorOpen;
                if(voiceEditorOpen) VoiceEditor();
                GUILayout.Space(12); if(GUILayout.Button(layoutEditor?"CLOSE LAYOUT SETTINGS":"WIDGETS / LAYOUT / APPEARANCE",buttonStyle)) layoutEditor=!layoutEditor;
                if(layoutEditor) LayoutEditor();
                if(!string.IsNullOrEmpty(saveError)) GUILayout.Label(saveError,labelStyle);
                GUILayout.EndScrollView(); GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                if(GUILayout.Button("PLACE SELECTED PANEL HERE",buttonStyle)) { if(PlaceHud!=null) PlaceHud(); else Place(); }
                if(GUILayout.Button("CALIBRATE",buttonStyle)) { if(desktopSettingsWindow) Calibrate?.Invoke(); else { IsEditing=false; Calibrate?.Invoke(); } }
                if(GUILayout.Button(desktopSettingsWindow?"CLOSE WINDOW":"CLOSE [F1]",buttonStyle)) { if(desktopSettingsWindow) WantsQuit=true; else ToggleEditor(); }
                if(GUILayout.Button("EXIT",buttonStyle)) WantsQuit=true;
                GUILayout.EndHorizontal(); GUILayout.EndArea(); GUI.EndGroup();
            }
            GUI.backgroundColor=Color.white; GUI.contentColor=Color.white;
        }
        static readonly string[] SettingsPages={"OVERVIEW","PANELS","DAILY","SCHOOL","DEVELOPER","AI","VOICE","APPEARANCE"};
        static readonly string[] SettingsDescriptions={"Your workspace at a glance","Choose panels and arrange them in space","Tasks and focus timer","Notes, flashcards and study timer","System status, notes and tasks","Ask your configured assistant","Offline voice commands","Panel look and on-screen hints"};
        void DrawDesktopSettings()
        {
            float width=Mathf.Min(Screen.width-32,1500),height=Mathf.Min(Screen.height-32,1120);
            float left=(Screen.width-width)*.5f,top=(Screen.height-height)*.5f;
            GUI.backgroundColor=Color.white;GUI.contentColor=green;
            GUILayout.BeginArea(new Rect(left,top,width,height),boxStyle);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUI.skin.box,GUILayout.Width(238),GUILayout.Height(height-40));
            GUILayout.Space(8);GUILayout.Label("XREAL\nCOMPANION",titleStyle);GUILayout.Label("SETTINGS CONSOLE",labelStyle);
            GUILayout.Space(14);
            for(int i=0;i<SettingsPages.Length;i++) {
                if(GUILayout.Button((settingsPage==i?"▸  ":"     ")+SettingsPages[i],buttonStyle,GUILayout.Height(43))) {
                    settingsPage=i;
                    if(i>=2&&i<=5)SetMode(i-2);
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label((tracking?"● TRACKING READY":"○ WAITING FOR TRACKING")+"\n"+(voiceListening?"MICROPHONE ACTIVE":"MICROPHONE OFF"),labelStyle);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true),GUILayout.Height(height-40));
            GUILayout.BeginHorizontal();GUILayout.BeginVertical();
            GUILayout.Label(SettingsPages[settingsPage],titleStyle);GUILayout.Label(SettingsDescriptions[settingsPage],labelStyle);
            GUILayout.EndVertical();GUILayout.FlexibleSpace();GUILayout.Label("SAVED AUTOMATICALLY",labelStyle);GUILayout.EndHorizontal();
            GUILayout.Space(8);GUILayout.Box("",GUILayout.ExpandWidth(true),GUILayout.Height(2));
            scroll=GUILayout.BeginScrollView(scroll,GUILayout.ExpandHeight(true));
            DrawSettingsPage();
            if(!string.IsNullOrEmpty(saveError))GUILayout.Label(saveError,labelStyle);
            GUILayout.EndScrollView();
            GUILayout.Space(8);GUILayout.Box("",GUILayout.ExpandWidth(true),GUILayout.Height(2));
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("PLACE ALL PANELS",buttonStyle,GUILayout.Height(52))) { if(PlaceAllHud!=null)PlaceAllHud();else PlaceAllPanelsAtGaze(); }
            if(GUILayout.Button("PLACE SELECTED",buttonStyle,GUILayout.Height(52))) { if(PlaceHud!=null)PlaceHud();else PlacePanelAtGaze(state.mode); }
            if(GUILayout.Button("CALIBRATE",buttonStyle,GUILayout.Height(52)))Calibrate?.Invoke();
            if(GUILayout.Button("CLOSE SETTINGS",buttonStyle,GUILayout.Height(52)))WantsQuit=true;
            GUILayout.EndHorizontal();GUILayout.EndVertical();
            GUILayout.EndHorizontal();GUILayout.EndArea();
            GUI.backgroundColor=Color.white;GUI.contentColor=Color.white;
        }
        void DrawSettingsPage()
        {
            switch(settingsPage) {
                case 0:
                    GUILayout.Label("Welcome back",titleStyle);
                    GUILayout.Label("Your panels are separate spatial windows. Turn each one on or off, then arrange the group or adjust a single panel.",labelStyle);
                    GUILayout.Space(16);
                    int visible=0;for(int i=0;i<4;i++)if(PanelVisible(i))visible++;
                    int open=state.tasks.FindAll(t=>!t.done).Count;
                    GUILayout.BeginHorizontal();GUILayout.Box("ACTIVE PANELS\n"+visible+" / 4",GUILayout.ExpandWidth(true),GUILayout.Height(90));GUILayout.Box("OPEN TASKS\n"+open,GUILayout.ExpandWidth(true),GUILayout.Height(90));GUILayout.Box("VOICE PHRASES\n"+state.voiceCommands.Count,GUILayout.ExpandWidth(true),GUILayout.Height(90));GUILayout.EndHorizontal();
                    GUILayout.Space(12);GUILayout.Label("QUICK START",titleStyle);
                    GUILayout.Label("1. Open Panels and choose what you want to see.\n2. Place all panels where you are looking, or tune each panel separately.\n3. Use Appearance to adjust readability and hide the keyboard hints.",labelStyle);
                    break;
                case 1: PanelSettingsPage();break;
                case 2: TimerEditor();TaskEditor();break;
                case 3: NotesEditor();FlashcardEditor();TimerEditor();break;
                case 4: GUILayout.Label(systemText,labelStyle);NotesEditor();TaskEditor();break;
                case 5: AiEditor();break;
                case 6: VoiceEditor();break;
                case 7: AppearanceSettingsPage();break;
            }
        }
        void PanelSettingsPage()
        {
            GUILayout.Label("Show the panels you want in your field of view.",labelStyle);
            for(int i=0;i<4;i++) {
                GUILayout.BeginHorizontal(GUI.skin.box);
                bool visible=GUILayout.Toggle(PanelVisible(i),Modes[i],labelStyle,GUILayout.Width(180));
                if(visible!=PanelVisible(i))SetPanelVisible(i,visible);
                if(GUILayout.Button("EDIT POSITION",buttonStyle,GUILayout.Width(180)))SetMode(i);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);GUILayout.Label("SELECTED PANEL  /  "+Modes[state.mode],titleStyle);
            var p=PanelState(state.mode);if(p==null)return;
            float x=Slider("Left / Right",p.x,-3.5f,3.5f),y=Slider("Height",p.y,-2.5f,2.5f),z=Slider("Distance",p.z,.5f,6f),scale=Slider("Panel size",p.scale,.65f,1.25f);
            if(x!=p.x||y!=p.y||z!=p.z||scale!=p.scale) {p.x=x;p.y=y;p.z=z;p.scale=scale;p.anchored=true;ApplyPanelPose(panelViews[state.mode]);MarkDirty();}
            GUILayout.Label("Positions save automatically. Use Place All Panels to move the whole layout together.",labelStyle);
            GUILayout.Space(12);GUILayout.Label("WIDGET LAYOUT",titleStyle);
            GUILayout.BeginHorizontal();for(int i=0;i<widgets.Count;i++)if(widgets[i].mode==state.mode&&GUILayout.Button((selected==i?"• ":"")+widgets[i].id.ToUpperInvariant(),buttonStyle))selected=i;GUILayout.EndHorizontal();
            var modeWidgets=widgets.FindAll(w=>w.mode==state.mode);
            if(modeWidgets.Count>0) {
                Widget w=modeWidgets.Find(v=>widgets.IndexOf(v)==selected);
                if(w==null){w=modeWidgets[0];selected=widgets.IndexOf(w);}
                var placement=state.profiles[state.mode].widgets.Find(v=>v!=null&&v.id==w.id);
                if(placement!=null) {
                    bool widgetVisible=GUILayout.Toggle(placement.visible,"Show "+w.id,labelStyle);if(widgetVisible!=placement.visible){placement.visible=widgetVisible;Apply(w,placement);MarkDirty();}
                    float wx=Slider("Widget horizontal",placement.x,-500,500),wy=Slider("Widget vertical",placement.y,-238,238),ws=Slider("Widget size",placement.scale,.65f,1.25f);
                    if(wx!=placement.x||wy!=placement.y||ws!=placement.scale){placement.x=wx;placement.y=wy;placement.scale=ws;Apply(w,placement);MarkDirty();}
                    if(GUILayout.Button("PLACE WIDGET WHERE I AM LOOKING",buttonStyle)&&PlaceWidgetAtGaze!=null)PlaceWidgetAtGaze(state.mode,w.id);
                }
            }
        }
        void AppearanceSettingsPage()
        {
            GUILayout.Label("TUNE THE HUD",titleStyle);
            GUILayout.Label("These controls affect the in-glasses panels. The desktop settings window stays readable.",labelStyle);
            bool hints=GUILayout.Toggle(state.showPanelHints,"Show bottom keyboard hints on panels",labelStyle);
            if(hints!=state.showPanelHints){state.showPanelHints=hints;Paint();MarkDirty();}
            GUILayout.Label("When hidden, the panel footer disappears. Keyboard shortcuts still work.",labelStyle);
            GUILayout.Space(14);
            GUILayout.Label("PANEL SIZE",titleStyle);GUILayout.BeginHorizontal();
            string[] presets={"COMPACT","READING","CODING"};for(int i=0;i<presets.Length;i++)if(GUILayout.Button((state.layoutPreset==i?"✓ ":"")+presets[i],buttonStyle)){state.layoutPreset=i;foreach(var view in panelViews)ApplyPanelPose(view);MarkDirty();}
            GUILayout.EndHorizontal();
            float brightness=Slider("Text brightness",state.brightness,.25f,1),opacity=Slider("Panel background",state.opacity,.35f,1),textScale=Slider("Text size",state.textScale,.8f,1.25f);
            if(brightness!=state.brightness||opacity!=state.opacity||textScale!=state.textScale){state.brightness=brightness;state.opacity=opacity;state.textScale=textScale;Paint();MarkDirty();}
        }
        void TimerEditor()
        {
            GUILayout.Label("FOCUS TIMER  /  "+TimeText(state.timerSeconds),titleStyle);
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(timerRunning?"PAUSE":"START",buttonStyle)) ToggleTimer();
            if(GUILayout.Button("FOCUS 25m",buttonStyle)) { state.timerMode=0; state.timerSeconds=1500; timerRunning=state.timerRunning=false; MarkDirty(); }
            if(GUILayout.Button("BREAK 5m",buttonStyle)) { state.timerMode=1; state.timerSeconds=300; timerRunning=state.timerRunning=false; MarkDirty(); }
            if(GUILayout.Button("RESET",buttonStyle)) { timerRunning=state.timerRunning=false; state.timerSeconds=state.timerMode==0?1500:300; MarkDirty(); }
            GUILayout.EndHorizontal();
        }
        void TaskEditor()
        {
            GUILayout.Space(12); GUILayout.Label("TASK QUEUE",titleStyle);
            GUILayout.BeginHorizontal(); taskDraft=GUILayout.TextField(taskDraft,140,fieldStyle);
            if(GUILayout.Button("ADD",buttonStyle,GUILayout.Width(85)) && !string.IsNullOrWhiteSpace(taskDraft) && state.tasks.Count<30) { state.tasks.Add(new TaskItem {title=taskDraft.Trim()}); taskDraft=""; MarkDirty(); }
            GUILayout.EndHorizontal();
            for(int i=0;i<state.tasks.Count;i++) {
                var task=state.tasks[i]; GUILayout.BeginHorizontal();
                bool done=GUILayout.Toggle(task.done,(task.done?"[x] ":"[ ] ")+task.title,labelStyle); if(done!=task.done) {task.done=done; MarkDirty();}
                if(GUILayout.Button("REMOVE",buttonStyle,GUILayout.Width(100))) {state.tasks.RemoveAt(i--); MarkDirty();}
                GUILayout.EndHorizontal();
            }
        }
        void NotesEditor()
        {
            GUILayout.Space(12); GUILayout.Label(state.mode==1?"STUDY NOTES":"DEVELOPER NOTES",titleStyle);
            string current=state.mode==2?state.developerNotes:state.notes;
            string notes=GUILayout.TextArea(current,8000,fieldStyle,GUILayout.MinHeight(130));
            if(notes!=current) {if(state.mode==2)state.developerNotes=notes;else state.notes=notes; MarkDirty();}
        }
        void FlashcardEditor()
        {
            GUILayout.Space(12); GUILayout.Label("FLASHCARDS",titleStyle);
            GUILayout.Label("Question",labelStyle); cardQuestion=GUILayout.TextField(cardQuestion,300,fieldStyle);
            GUILayout.Label("Answer",labelStyle); cardAnswer=GUILayout.TextArea(cardAnswer,1000,fieldStyle,GUILayout.MinHeight(60));
            if(GUILayout.Button("ADD CARD",buttonStyle) && !string.IsNullOrWhiteSpace(cardQuestion) && state.cards.Count<100) { state.cards.Add(new Flashcard {question=cardQuestion.Trim(),answer=cardAnswer.Trim()}); cardQuestion=cardAnswer=""; cardIndex=state.cards.Count-1; back=false; MarkDirty(); }
            if(state.cards.Count>0) {
                GUILayout.Label($"Card {cardIndex+1} / {state.cards.Count}: "+state.cards[cardIndex].question,labelStyle);
                GUILayout.BeginHorizontal(); if(GUILayout.Button("PREVIOUS",buttonStyle)) NextCard(-1); if(GUILayout.Button(back?"QUESTION":"ANSWER",buttonStyle)) back=!back;
                if(GUILayout.Button("NEXT",buttonStyle)) NextCard(1);
                if(GUILayout.Button("DELETE CARD",buttonStyle)) {state.cards.RemoveAt(cardIndex); cardIndex=Mathf.Clamp(cardIndex,0,Mathf.Max(0,state.cards.Count-1)); back=false; MarkDirty();} GUILayout.EndHorizontal();
            }
        }
        float PresetScale() => state.layoutPreset==0?.00105f:state.layoutPreset==2?.0013f:.0012f;
        void LayoutEditor()
        {
            GUILayout.Label("SELECTED PANEL / "+Modes[state.mode]+"",labelStyle);
            var panelState=PanelState(state.mode);
            bool panelVisible=GUILayout.Toggle(panelState.visible,"Show this panel",labelStyle);if(panelVisible!=panelState.visible)SetPanelVisible(state.mode,panelVisible);
            GUILayout.Label("PANEL POSITION  /  meters from the tracking origin",labelStyle);
            float panelX=Slider("Left / Right",panelState.x,-3.5f,3.5f),panelY=Slider("Height",panelState.y,-2.5f,2.5f),panelZ=Slider("Distance",panelState.z,.5f,6f);
            float panelScale=Slider("Panel size",panelState.scale,.65f,1.25f);
            if(panelX!=panelState.x||panelY!=panelState.y||panelZ!=panelState.z||panelScale!=panelState.scale) {
                panelState.x=panelX;panelState.y=panelY;panelState.z=panelZ;panelState.scale=panelScale;panelState.anchored=true;ApplyPanelPose(panelViews[state.mode]);MarkDirty();
            }
            GUILayout.Label("HUD SIZE PRESET",labelStyle);GUILayout.BeginHorizontal();
            string[] presets={"COMPACT","READING","CODING"};for(int i=0;i<3;i++) if(GUILayout.Button((state.layoutPreset==i?"> ":"")+presets[i],buttonStyle)){state.layoutPreset=i;foreach(var view in panelViews)ApplyPanelPose(view);MarkDirty();}GUILayout.EndHorizontal();
            GUILayout.Label("PROFILE: "+Modes[state.mode]+" / saved automatically",labelStyle);
            GUILayout.BeginHorizontal(); for(int i=0;i<widgets.Count;i++) if(GUILayout.Button((selected==i?"> ":"")+widgets[i].id.ToUpperInvariant(),buttonStyle)) selected=i; GUILayout.EndHorizontal();
            if(widgets.Count>0) {
                selected=Mathf.Clamp(selected,0,widgets.Count-1); Widget w=widgets[selected]; var p=state.profiles[state.mode].widgets.Find(v=>v.id==w.id);
                bool visible=GUILayout.Toggle(p.visible,(p.visible?"[x] ":"[ ] ")+"Visible",labelStyle); if(visible!=p.visible) {p.visible=visible; MarkDirty();}
                float x=Slider("Horizontal",p.x,-500,500), y=Slider("Vertical",p.y,-238,238),scale=Slider("Widget size",p.scale,.65f,1.25f);
                if(x!=p.x || y!=p.y || scale!=p.scale) {p.x=x;p.y=y;p.scale=scale;Apply(w,p); MarkDirty();}
                if(GUILayout.Button("PLACE SELECTED AT GAZE",buttonStyle)) {
                    if(PlaceWidgetAtGaze!=null) PlaceWidgetAtGaze(state.mode,w.id);
                    else PlaceWidgetLocally(w,p);
                }
            }
            float brightness=Slider("Brightness",state.brightness,.25f,1),opacity=Slider("Background",state.opacity,.35f,1),textScale=Slider("Text size",state.textScale,.8f,1.25f);
            if(brightness!=state.brightness || opacity!=state.opacity || textScale!=state.textScale) {state.brightness=brightness;state.opacity=opacity;state.textScale=textScale;Paint();MarkDirty();}
            if(GUILayout.Button("PLACE PANEL AT GAZE",buttonStyle)) {if(PlaceHud!=null)PlaceHud();else PlacePanelAtGaze(state.mode);}
            if(GUILayout.Button("RESET THIS PROFILE LAYOUT",buttonStyle)) {state.profiles[state.mode].widgets.Clear();Rebuild();MarkDirty();}
        }
        void PlaceWidgetLocally(Widget widget,Placement placement)
        {
            var gaze=new Ray(head.position,head.forward);
            if(new Plane(Root.forward,Root.position).Raycast(gaze,out float distance) && distance>0) {
                Vector3 point=Root.InverseTransformPoint(gaze.GetPoint(distance));placement.x=point.x;placement.y=point.y;Apply(widget,placement);MarkDirty();
            } else Notify("Look toward the HUD to place a widget.");
        }
        public void PlaceWidgetAtGazeNow(int mode,string id)
        {
            if(mode<0 || mode>=state.profiles.Count || Root==null || head==null) return;
            if(state.mode!=mode) SetMode(mode);
            var widget=widgets.Find(w=>w.id==id);var placement=state.profiles[mode].widgets.Find(p=>p!=null && p.id==id);
            if(widget==null || placement==null) return;
            PlaceWidgetLocally(widget,placement);
        }
        float Slider(string title,float value,float min,float max) { GUILayout.Label(title+"  "+value.ToString("0.00"),labelStyle);return GUILayout.HorizontalSlider(value,min,max); }
        void AiEditor()
        {
            GUILayout.Label("AI CONNECTION",titleStyle); GUILayout.Label("HTTPS OpenAI-compatible chat-completions URL",labelStyle);
            string endpoint=GUILayout.TextField(state.endpoint,500,fieldStyle); if(endpoint!=state.endpoint) {state.endpoint=endpoint;MarkDirty();}
            GUILayout.Label("Model",labelStyle); string model=GUILayout.TextField(state.model,150,fieldStyle);if(model!=state.model){state.model=model;MarkDirty();}
            GUILayout.Label("API key (this session only; never saved)",labelStyle);apiKey=GUILayout.PasswordField(apiKey,'*',fieldStyle);
            GUILayout.Label("Question",labelStyle);prompt=GUILayout.TextArea(prompt,4000,fieldStyle,GUILayout.MinHeight(90));
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(request==null?"ASK":"SENDING...",buttonStyle) && request==null) StartCoroutine(Ask());
            if(request!=null && GUILayout.Button("CANCEL",buttonStyle)) request.Abort();
            if(GUILayout.Button("PREVIOUS PAGE",buttonStyle)) answerPage=Mathf.Max(0,answerPage-1);
            if(GUILayout.Button("NEXT PAGE",buttonStyle)) answerPage=Mathf.Min(Mathf.Max(0,(answer.Length-1)/800),answerPage+1);
            GUILayout.EndHorizontal();GUILayout.Label(aiStatus,labelStyle);if(!string.IsNullOrEmpty(answer)) GUILayout.Label(answer,labelStyle);
        }
        void VoiceEditor()
        {
            GUILayout.Space(10);GUILayout.Label("VOICE COMMANDS / LOCAL ENGLISH RECOGNITION",titleStyle);
            GUILayout.Label("Audio is processed on this PC and is never uploaded or saved. Hold M to talk; release M to stop. The button can also leave listening on.",labelStyle);
            string[] microphones=Microphone.devices;
            GUILayout.BeginHorizontal();GUILayout.Label("INPUT: "+(string.IsNullOrEmpty(state.voiceDevice)?"SYSTEM DEFAULT":state.voiceDevice),labelStyle);
            if(microphones.Length>0 && GUILayout.Button("NEXT MIC",buttonStyle,GUILayout.Width(140))) {
                int current=Array.IndexOf(microphones,state.voiceDevice);state.voiceDevice=microphones[(current+1+microphones.Length)%microphones.Length];MarkDirty();
            }
            GUILayout.EndHorizontal();
            if(GUILayout.Button(voiceListening?"STOP LISTENING":"START LISTENING",buttonStyle)) {if(voiceListening)StopVoiceListening();else StartVoiceListening();}
            GUILayout.Label(voiceStatus,labelStyle);
            if(!string.IsNullOrEmpty(lastVoiceTranscript))GUILayout.Label("LAST HEARD: "+lastVoiceTranscript,labelStyle);
            GUILayout.Space(8);GUILayout.Label("ADD A PHRASE AND ACTION",titleStyle);
            voicePhraseDraft=GUILayout.TextField(voicePhraseDraft,120,fieldStyle);
            GUILayout.Label("ACTION",labelStyle);
            voiceActionDraft=GUILayout.SelectionGrid(voiceActionDraft,VoiceActions,Mathf.Min(3,VoiceActions.Length),buttonStyle);
            GUILayout.Label(voiceActionDraft==0?"Task number (1 = first task)":voiceActionDraft==2||voiceActionDraft==3?"Panel number (1 Daily, 2 School, 3 Developer, 4 AI)":voiceActionDraft==4?"Section number (1 Daily, 2 School, 3 Developer, 4 AI)":"Target not used",labelStyle);
            voiceTargetDraft=GUILayout.TextField(voiceTargetDraft,3,fieldStyle);
            if(GUILayout.Button("ADD VOICE COMMAND",buttonStyle)) {
                if(string.IsNullOrWhiteSpace(voicePhraseDraft)) voiceStatus="Enter the words you want to say.";
                else if(!int.TryParse(voiceTargetDraft,out int target) || target<1 || target>(voiceActionDraft==0?30:4)) voiceStatus="Enter a valid target number.";
                else if(state.voiceCommands.Count>=50) voiceStatus="The command list is full (50 maximum).";
                else if(state.voiceCommands.Exists(c=>c!=null && NormalizeCommand(c.phrase)==NormalizeCommand(voicePhraseDraft))) voiceStatus="That phrase is already in the list.";
                else {state.voiceCommands.Add(new VoiceCommand {phrase=voicePhraseDraft.Trim(),action=voiceActionDraft,target=target});MarkDirty();voiceStatus="Saved. Say the phrase while listening is on.";}
            }
            GUILayout.Space(8);GUILayout.Label("SAVED PHRASES",titleStyle);
            for(int i=0;i<state.voiceCommands.Count;i++) {
                var command=state.voiceCommands[i];GUILayout.BeginHorizontal();
                bool enabled=GUILayout.Toggle(command.enabled,command.phrase,labelStyle,GUILayout.MinWidth(220));if(enabled!=command.enabled){command.enabled=enabled;MarkDirty();}
                GUILayout.Label(VoiceActions[Mathf.Clamp(command.action,0,VoiceActions.Length-1)]+(command.action==1?"": " / "+command.target),labelStyle,GUILayout.MinWidth(200));
                if(GUILayout.Button("REMOVE",buttonStyle,GUILayout.Width(110))){state.voiceCommands.RemoveAt(i--);MarkDirty();}
                GUILayout.EndHorizontal();
            }
        }
        IEnumerator Ask()
        {
            if(!Uri.TryCreate(state.endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !string.IsNullOrEmpty(uri.UserInfo)) {aiStatus="Use an HTTPS chat-completions URL.";yield break;}
            if(string.IsNullOrWhiteSpace(prompt)||string.IsNullOrWhiteSpace(state.model)) {aiStatus="Enter a model and a question first.";yield break;}
            var body=new ChatRequest {model=state.model,messages=new[] {new Message {role="system",content="Give concise, accurate answers for a glasses HUD. Use plain text. Do not claim access to the user's files, screen, tasks or sensors."},new Message {role="user",content=prompt}}};
            request=new UnityWebRequest(uri.AbsoluteUri,"POST");request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));request.downloadHandler=new DownloadHandlerBuffer();request.timeout=45;
            request.SetRequestHeader("Content-Type","application/json");if(!string.IsNullOrWhiteSpace(apiKey)) request.SetRequestHeader("Authorization","Bearer "+apiKey.Trim());
            aiStatus="Waiting for provider...";
            yield return request.SendWebRequest();
            try {
                if(request.result!=UnityWebRequest.Result.Success) aiStatus="Request failed: "+request.responseCode+" "+request.error;
                else {var response=JsonUtility.FromJson<ChatResponse>(request.downloadHandler.text);if(response?.choices==null || response.choices.Length==0 || response.choices[0].message==null) aiStatus="Provider returned no chat answer.";else {answer=Limit(response.choices[0].message.content,12000);answerPage=0;aiStatus="Answer received. Close controls to read it in the HUD.";AiAnswerChanged?.Invoke(answer);}}
            } catch {aiStatus="Provider response was not valid chat-completions JSON.";}
            request.Dispose();request=null;Paint();
        }
        public void SetExternalAnswer(string value) { answer=Limit(value??"",12000);answerPage=0;aiStatus="Answer received from desktop settings.";Paint(); }
        [StructLayout(LayoutKind.Sequential)] struct Memory {public uint length,load;public ulong total,available,totalPage,availablePage,totalVirtual,availableVirtual,extended;}
        [StructLayout(LayoutKind.Sequential)] struct Power {public byte ac,battery,percent,reserved;public uint lifetime,fullLifetime;}
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref Memory memory);
        [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out Power power);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
        long lastIdle,lastTotal;
        string SystemSnapshot()
        {
            string cpu="--"; if(GetSystemTimes(out long idle,out long kernel,out long user)) {long total=kernel+user;if(lastTotal!=0 && total>lastTotal) cpu=Mathf.Clamp((float)(100.0*(1.0-(idle-lastIdle)/(double)(total-lastTotal))),0,100).ToString("0")+"%";lastIdle=idle;lastTotal=total;}
            var m=new Memory {length=(uint)Marshal.SizeOf(typeof(Memory))};bool memory=GlobalMemoryStatusEx(ref m);
            string battery=GetSystemPowerStatus(out Power p)?(p.ac==1?"AC POWER":p.percent<=100?p.percent+"% BATTERY":"BATTERY N/A"):"POWER N/A";
            return "CPU     "+cpu+"\nMEMORY  "+(memory?((m.total-m.available)/1073741824.0).ToString("0.0")+" / "+(m.total/1073741824.0).ToString("0.0")+" GB":"N/A")+"\n"+battery+"\nUSB     "+(usb?"CONNECTED":"WAITING");
        }
        void OnApplicationQuit() {if(Root!=null) Save();}
        void OnDestroy()
        {
            if(request!=null){request.Abort();request.Dispose();request=null;}
            voiceListening=false;voiceStopRequested=true;
            if(microphoneClip!=null){try{Microphone.End(microphoneName);}catch{}microphoneClip=null;}
            if(voiceWorker!=null && voiceWorker.IsAlive)voiceWorker.Join(2000);
            if(voiceWorker==null || !voiceWorker.IsAlive) {if(voiceModel!=null){voiceModel.Dispose();voiceModel=null;}}
        }

        public IEnumerator VerifyPreviewFeatures()
        {
            if(!Preview) throw new InvalidOperationException("Checks require isolated preview mode.");
            var original=state; string originalPath=path;
            string checkPath=Path.Combine(Application.temporaryCachePath,"companion-check-"+Guid.NewGuid().ToString("N")+".json");
            try {
                state=new Preferences(); Sanitize(); path=checkPath; Preview=false;
                state.tasks.Add(new TaskItem {title="Check task",done=true});
                state.notes="Study note check";state.developerNotes="Developer note check";
                state.cards.Add(new Flashcard {question="Question check",answer="Answer check"});
                SetMode(1);
                var placement=state.profiles[1].widgets.Find(w=>w.id=="notes");
                placement.x=12;placement.y=8;placement.scale=.8f;placement.visible=false;
                state.timerSeconds=.3f;timerRunning=true;
                yield return new WaitForSecondsRealtime(.6f);
                if(timerRunning || state.timerSeconds!=0) throw new InvalidOperationException("Timer completion failed.");
                apiKey="session-key-not-for-disk";Save();
                if(!File.Exists(path) || File.ReadAllText(path).Contains(apiKey)) throw new InvalidOperationException("Preference save/key isolation failed.");
                state=new Preferences();Load();Sanitize();
                placement=state.profiles[1].widgets.Find(w=>w.id=="notes");
                if(state.tasks.Count!=1 || !state.tasks[0].done || state.cards.Count!=1 || state.notes!="Study note check" || state.developerNotes!="Developer note check" || placement==null || placement.visible || placement.scale!=.8f) throw new InvalidOperationException("Saved feature roundtrip failed.");
                state.endpoint="http://example.com/v1/chat/completions";state.model="check";prompt="check";
                yield return Ask();
                if(request!=null || !aiStatus.Contains("HTTPS")) throw new InvalidOperationException("AI HTTPS guard failed.");
                Debug.Log("[HUD Checks] PASS: timer completion, tasks, separate notes, flashcards, profile placement, saved preferences, session-only key, AI HTTPS guard.");
            } finally {
                Preview=true;state=original;path=originalPath;apiKey=prompt="";aiStatus="Configure a provider in F1 to ask a question.";
                timerRunning=false;dirty=false;toastUntil=0;cardIndex=0;Rebuild();
                foreach(string file in new[]{checkPath,checkPath+".bak",checkPath+".tmp"}) if(File.Exists(file)) File.Delete(file);
            }
        }
    }
}




