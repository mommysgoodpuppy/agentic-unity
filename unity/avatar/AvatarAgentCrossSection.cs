// Slice the avatar's actual posed meshes, to see clipping and fit that a render hides. Read-only.
// Headless, with the project closed (then plot it with tools/plot_avatar_sections.py):
//   Unity -batchmode -force-vulkan -projectPath <proj> -executeMethod AvatarAgentCrossSection.Export \
//     -avatarScene Assets/....unity -out <file.json> [-renderers Hat,Hair,Body] [-anchor Head] -logFile <log>
// Renderers are matched by name or by path from the avatar root; without -renderers, every active skinned
// mesh is sliced. Coordinates are metres from the -anchor humanoid bone, on the avatar's axes.
// Live: Tools > Avatar Agent > Cross Section, or write Library/VRUnityAgent/CrossSections/request.json:
//   {"command":"capture","label":"name","renderers":["Hat"],"anchor":"Head"}   a snapshot to <label>.json
//   {"command":"open"}                                                         also opens the window
//   {"command":"capture-view","label":"name","axis":0,"planeOffset":0,"clip":true}   a render to <label>-render.png
// Results and errors go to that folder (last-result.txt, error.txt).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

[InitializeOnLoad]
public static class AvatarAgentCrossSection
{
    [Serializable] public class Sample { public string label,path; public Color color; public Vector3[] vertices; public int[] triangles; }
    [Serializable] public class Segment { public Vector2 a,b; }
    [Serializable] public class Layer { public string label; public Color color; public Segment[] segments; }
    [Serializable] public class Section { public string label; public int axis; public float offset; public Layer[] layers; }
    [Serializable] public class Snapshot { public Vector3 headOrigin; public Sample[] meshes; public Section[] sections; }
    [Serializable] class Request { public string command,label; public int axis; public float planeOffset; public bool clip; public string anchor; public string[] renderers; }
    public static string Work => Path.GetFullPath(Path.Combine(Application.dataPath,"../Library/VRUnityAgent/CrossSections"));
    static AvatarAgentCrossSection() { EditorApplication.update += Poll; }
    static void Poll()
    {
        if(EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlaying)return;
        string request=Path.Combine(Work,"request.json");if(!File.Exists(request))return;
        var input=JsonUtility.FromJson<Request>(File.ReadAllText(request));File.Delete(request);
        try
        {
            if(input.command=="capture-view")AvatarCrossSectionWindow.CaptureView(input.label,input.axis,input.planeOffset,input.clip);
            else
            {
                Directory.CreateDirectory(Work);
                var data=Capture(Avatar(),input.renderers,input.anchor);
                string label=string.IsNullOrEmpty(input.label)?"sections":input.label;
                if(label.IndexOfAny(Path.GetInvalidFileNameChars())>=0||label.Contains(".."))throw new Exception("Invalid label");
                string output=Path.Combine(Work,label+".json");
                if(File.Exists(output))throw new IOException("Snapshot exists: "+output);
                File.WriteAllText(output,JsonUtility.ToJson(data));
                if(input.command=="open")AvatarCrossSectionWindow.Open();
                File.WriteAllText(Path.Combine(Work,"last-result.txt"),output);
            }
        }
        catch(Exception e){File.WriteAllText(Path.Combine(Work,"error.txt"),e.ToString());}
    }
    public static GameObject Avatar()
    {
        var selected=Selection.activeGameObject?Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>():null;
        if(selected&&selected.gameObject.scene.IsValid())return selected.gameObject;
        return SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
    }
    static string Arg(string name){var a=Environment.GetCommandLineArgs();int i=Array.IndexOf(a,name);return i>=0&&i+1<a.Length?a[i+1]:null;}
    public static void Export()
    {
        int code=0;
        try
        {
            string output=Arg("-out")??throw new ArgumentException("Pass -out file.json");
            var scene=EditorSceneManager.OpenScene(Arg("-avatarScene")??throw new ArgumentException("Pass -avatarScene"),OpenSceneMode.Single);
            var avatar=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
            var data=Capture(avatar,Arg("-renderers")?.Split(','),Arg("-anchor"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output,JsonUtility.ToJson(data));
            Debug.Log($"[AvatarAgentCrossSection] wrote {output}: {string.Join(", ",data.meshes.Select(m=>m.label))}");
        }
        catch(Exception e){Debug.LogException(e);code=1;}
        EditorApplication.Exit(code);
    }
    static readonly Color[] Palette={new Color(.2f,.85f,1),new Color(1,.25f,.65f),new Color(1,.8f,.2f),new Color(.45f,.95f,.45f),new Color(1,.45f,.1f),new Color(.7f,.55f,1),new Color(1,1,1),new Color(.6f,.75f,.3f)};
    public static Snapshot Capture(GameObject avatar,string[] renderers=null,string anchor=null)
    {
        var boneName=string.IsNullOrEmpty(anchor)?HumanBodyBones.Head:(HumanBodyBones)Enum.Parse(typeof(HumanBodyBones),anchor);
        var bone=avatar.GetComponent<Animator>().GetBoneTransform(boneName);
        if(!bone)throw new Exception("Avatar has no humanoid "+boneName);
        var origin=avatar.transform.InverseTransformPoint(bone.position);
        var wanted=renderers==null||renderers.Length==0?null:renderers.Select(r=>r.Trim()).Where(r=>r.Length>0).ToArray();
        var samples=new List<Sample>();
        foreach(var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if(!renderer.enabled||!renderer.gameObject.activeInHierarchy||renderer.sharedMesh==null)continue;
            string path=AnimationUtility.CalculateTransformPath(renderer.transform,avatar.transform);
            if(wanted!=null&&!wanted.Contains(renderer.name)&&!wanted.Contains(path))continue;
            var inverse=avatar.transform.worldToLocalMatrix;
            samples.Add(new Sample{label=renderer.name,path=path,color=Palette[samples.Count%Palette.Length],vertices=AvatarMeshGeometry.WorldVertices(renderer).Select(v=>inverse.MultiplyPoint3x4(v)-origin).ToArray(),triangles=renderer.sharedMesh.triangles});
        }
        if(samples.Count==0)throw new Exception(wanted==null?"No active skinned meshes":"No active skinned mesh matches: "+string.Join(", ",wanted));
        var specs=new[]{(0,0f),(0,-.04f),(0,.04f),(2,0f),(2,-.05f),(2,.05f),(1,.05f),(1,.10f),(1,.15f)};
        return new Snapshot{headOrigin=origin,meshes=samples.ToArray(),sections=specs.Select(s=>MakeSection(samples.ToArray(),s.Item1,s.Item2)).ToArray()};
    }
    public static Section MakeSection(Sample[] samples,int axis,float offset)
    {
        return new Section{label="XYZ"[axis]+"="+offset.ToString("F3"),axis=axis,offset=offset,layers=samples.Select(m=>new Layer{label=m.label,color=m.color,segments=Slice(m,axis,offset).ToArray()}).ToArray()};
    }
    public static Vector2 Project(Vector3 point,int axis) => axis==0?new Vector2(point.z,point.y):axis==1?new Vector2(point.x,point.z):new Vector2(point.x,point.y);
    public static IEnumerable<Segment> Slice(Sample sample,int axis,float offset)
    {
        const float epsilon=1e-7f;
        var points=new List<Vector3>(3);
        for(int i=0;i<sample.triangles.Length;i+=3)
        {
            var a=sample.vertices[sample.triangles[i]];var b=sample.vertices[sample.triangles[i+1]];var c=sample.vertices[sample.triangles[i+2]];
            if(Mathf.Abs(a[axis]-offset)<epsilon&&Mathf.Abs(b[axis]-offset)<epsilon&&Mathf.Abs(c[axis]-offset)<epsilon)continue;
            points.Clear();Edge(a,b);Edge(b,c);Edge(c,a);
            if(points.Count==2&&(points[0]-points[1]).sqrMagnitude>epsilon*epsilon)yield return new Segment{a=Project(points[0],axis),b=Project(points[1],axis)};
            void Add(Vector3 p){if(!points.Any(q=>(p-q).sqrMagnitude<epsilon*epsilon))points.Add(p);}
            void Edge(Vector3 p,Vector3 q)
            {
                float dp=p[axis]-offset,dq=q[axis]-offset;
                if(Mathf.Abs(dp)<epsilon)Add(p);
                if(Mathf.Abs(dq)<epsilon)Add(q);
                if((dp< -epsilon&&dq>epsilon)||(dp>epsilon&&dq< -epsilon))Add(Vector3.LerpUnclamped(p,q,dp/(dp-dq)));
            }
        }
    }
}

public class AvatarCrossSectionWindow:EditorWindow
{
    GameObject avatar;
    AvatarAgentCrossSection.Snapshot snapshot;
    AvatarAgentCrossSection.Section section;
    int axis;float offset;bool sceneOverlay=true,clipScene;string renderers="";
    double nextRefresh;
    [MenuItem("Tools/Avatar Agent/Cross Section")]
    public static void Open(){var win=GetWindow<AvatarCrossSectionWindow>("Mesh Cross Section");win.minSize=new Vector2(500,560);win.avatar=AvatarAgentCrossSection.Avatar();win.Refresh();win.Show();}
    static void SetClipShader(Shader shader,string tag){foreach(SceneView view in SceneView.sceneViews)view.SetSceneViewShaderReplace(shader,tag);}
    public static void CaptureView(string label,int axis,float offset,bool clip)
    {
        if(string.IsNullOrEmpty(label)||label.IndexOfAny(Path.GetInvalidFileNameChars())>=0||label.Contains(".."))throw new Exception("Invalid view label");
        var win=GetWindow<AvatarCrossSectionWindow>("Mesh Cross Section");win.avatar=AvatarAgentCrossSection.Avatar();win.axis=axis;win.offset=offset;win.clipScene=clip;win.Refresh();
        SetClipShader(clip?Shader.Find("Hidden/VRUnity/EditorSectionClip"):null,null);
        var view=SceneView.lastActiveSceneView??GetWindow<SceneView>();
        var direction=axis==0?win.avatar.transform.right:axis==1?win.avatar.transform.up:win.avatar.transform.forward;
        var up=axis==1?win.avatar.transform.forward:win.avatar.transform.up;
        var point=win.avatar.transform.TransformPoint(win.snapshot.headOrigin+new Vector3(0,.15f,-.03f));
        view.sceneLighting=false;view.cameraMode=SceneView.GetBuiltinCameraMode(DrawCameraMode.Textured);
        view.LookAt(point,Quaternion.LookRotation(-direction,up),.39f,true,true);view.Show();view.Focus();view.Repaint();
        double when=EditorApplication.timeSinceStartup+1.5;
        EditorApplication.CallbackFunction capture=null;
        capture=()=>
        {
            if(EditorApplication.timeSinceStartup<when)return;EditorApplication.update-=capture;
            try
            {
                CaptureSurfaceImage(view,win,label);
            }
            catch(Exception e){File.WriteAllText(Path.Combine(AvatarAgentCrossSection.Work,"capture-error.txt"),e.ToString());}
        };
        EditorApplication.update+=capture;
    }
    static void CaptureSurfaceImage(SceneView view,AvatarCrossSectionWindow win,string label)
    {
        int width=1200,height=Mathf.RoundToInt(width/Mathf.Max(view.camera.aspect,.1f));
        var go=new GameObject("Section verification camera"){hideFlags=HideFlags.HideAndDontSave};
        var camera=go.AddComponent<Camera>();camera.enabled=false;camera.CopyFrom(view.camera);
        var texture=RenderTexture.GetTemporary(width,height,24,RenderTextureFormat.ARGB32);
        var previous=RenderTexture.active;var lineMaterial=new Material(Shader.Find("Hidden/Internal-Colored"));
        try
        {
            camera.targetTexture=texture;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.12f,.14f,.17f);
            var normal=win.axis==0?win.avatar.transform.right:win.axis==1?win.avatar.transform.up:win.avatar.transform.forward;
            var centre=win.snapshot.headOrigin;centre[win.axis]+=win.offset;var worldPoint=win.avatar.transform.TransformPoint(centre);
            Shader.SetGlobalVector("_VRUnitySectionPlane",new Vector4(-normal.x,-normal.y,-normal.z,Vector3.Dot(normal,worldPoint)));
            camera.RenderWithShader(Shader.Find("Hidden/VRUnity/EditorSectionClip"),"");
            RenderTexture.active=texture;
            lineMaterial.SetInt("_SrcBlend",(int)UnityEngine.Rendering.BlendMode.SrcAlpha);lineMaterial.SetInt("_DstBlend",(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull",0);lineMaterial.SetInt("_ZWrite",0);lineMaterial.SetInt("_ZTest",(int)UnityEngine.Rendering.CompareFunction.Always);
            Vector3 World(Vector2 v){var p=win.axis==0?new Vector3(win.offset,v.y,v.x):win.axis==1?new Vector3(v.x,win.offset,v.y):new Vector3(v.x,v.y,win.offset);return win.avatar.transform.TransformPoint(p+win.snapshot.headOrigin);}
            GL.PushMatrix();GL.LoadProjectionMatrix(camera.projectionMatrix);GL.modelview=camera.worldToCameraMatrix;lineMaterial.SetPass(0);GL.Begin(GL.LINES);
            foreach(var layer in win.section.layers){GL.Color(layer.color);foreach(var line in layer.segments){GL.Vertex(World(line.a));GL.Vertex(World(line.b));}}
            GL.End();GL.PopMatrix();
            var image=new Texture2D(width,height,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();
            File.WriteAllBytes(Path.Combine(AvatarAgentCrossSection.Work,label+"-render.png"),image.EncodeToPNG());UnityEngine.Object.DestroyImmediate(image);
        }
        finally{RenderTexture.active=previous;camera.targetTexture=null;RenderTexture.ReleaseTemporary(texture);UnityEngine.Object.DestroyImmediate(lineMaterial);UnityEngine.Object.DestroyImmediate(go);}
    }
    void OnEnable(){SceneView.duringSceneGui+=DrawScene;EditorApplication.update+=RefreshLive;}
    void RefreshLive()
    {
        if(!avatar||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlaying||EditorApplication.timeSinceStartup<nextRefresh)return;
        nextRefresh=EditorApplication.timeSinceStartup+.4;
        try{Refresh();}catch(Exception e){Debug.LogWarning("Cross-section refresh: "+e.Message);}
    }
    void OnDisable(){SceneView.duringSceneGui-=DrawScene;EditorApplication.update-=RefreshLive;SetClipShader(null,null);}
    void Refresh(){if(!avatar)return;snapshot=AvatarAgentCrossSection.Capture(avatar,renderers.Split(','),null);Rebuild();}
    void Rebuild(){if(snapshot!=null)section=AvatarAgentCrossSection.MakeSection(snapshot.meshes,axis,offset);Repaint();SceneView.RepaintAll();}
    void OnGUI()
    {
        EditorGUILayout.LabelField("Actual baked geometry · coordinates relative to Head",EditorStyles.boldLabel);
        renderers=EditorGUILayout.TextField("Renderers (comma separated; empty = all)",renderers);
        var next=(GameObject)EditorGUILayout.ObjectField("Avatar",avatar,typeof(GameObject),true);
        if(next!=avatar){avatar=next;Refresh();}
        EditorGUI.BeginChangeCheck();
        axis=GUILayout.Toolbar(axis,new[]{"Side slice (X)","Horizontal (Y)","Front slice (Z)"});
        offset=EditorGUILayout.Slider("Offset (metres)",offset,-.5f,.5f);
        sceneOverlay=EditorGUILayout.Toggle("Show plane in Scene view",sceneOverlay);
        clipScene=EditorGUILayout.Toggle("Clip mesh at plane (editor only)",clipScene);
        if(EditorGUI.EndChangeCheck()){SetClipShader(clipScene?Shader.Find("Hidden/VRUnity/EditorSectionClip"):null,null);Rebuild();}
        EditorGUILayout.BeginHorizontal();
        if(GUILayout.Button("Refresh baked meshes"))Refresh();
        if(GUILayout.Button("Centre plane")){offset=0;Rebuild();}
        if(GUILayout.Button("Export snapshot"))
        {
            Directory.CreateDirectory(AvatarAgentCrossSection.Work);
            File.WriteAllText(Path.Combine(AvatarAgentCrossSection.Work,"manual-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".json"),JsonUtility.ToJson(snapshot));
        }
        EditorGUILayout.EndHorizontal();
        if(section==null)return;
        foreach(var layer in section.layers){var old=GUI.contentColor;GUI.contentColor=layer.color;EditorGUILayout.LabelField(layer.label);GUI.contentColor=old;}
        Rect canvas=GUILayoutUtility.GetRect(100,100,GUILayout.ExpandWidth(true),GUILayout.ExpandHeight(true));
        EditorGUI.DrawRect(canvas,new Color(.065f,.075f,.09f));
        var all=section.layers.SelectMany(l=>l.segments).SelectMany(s=>new[]{s.a,s.b}).ToArray();if(all.Length==0)return;
        Vector2 lo=new Vector2(all.Min(p=>p.x),all.Min(p=>p.y)),hi=new Vector2(all.Max(p=>p.x),all.Max(p=>p.y));
        float scale=Mathf.Min((canvas.width-40)/Mathf.Max(hi.x-lo.x,.01f),(canvas.height-55)/Mathf.Max(hi.y-lo.y,.01f));var centre=(lo+hi)/2;
        Vector3 Map(Vector2 p)=>new Vector3(canvas.center.x+(p.x-centre.x)*scale,canvas.center.y-(p.y-centre.y)*scale,0);
        Handles.BeginGUI();
        foreach(var layer in section.layers){Handles.color=layer.color;foreach(var segment in layer.segments)Handles.DrawAAPolyLine(2.3f,Map(segment.a),Map(segment.b));}
        Handles.color=new Color(.3f,.4f,.5f);float bar=.05f*scale;Handles.DrawLine(new Vector3(canvas.x+20,canvas.yMax-20),new Vector3(canvas.x+20+bar,canvas.yMax-20));
        Handles.EndGUI();GUI.Label(new Rect(canvas.x+20,canvas.yMax-45,180,22),"50 mm · "+(axis==0?"right = front":axis==1?"up = front":"up = head top"));
    }
    void DrawScene(SceneView view)
    {
        if(!sceneOverlay||section==null||!avatar)return;
        var origin=snapshot.headOrigin;
        Vector3 Unproject(Vector2 v){var p=axis==0?new Vector3(offset,v.y,v.x):axis==1?new Vector3(v.x,offset,v.y):new Vector3(v.x,v.y,offset);return avatar.transform.TransformPoint(p+origin);}
        foreach(var layer in section.layers){Handles.color=layer.color;foreach(var s in layer.segments)Handles.DrawAAPolyLine(2.5f,Unproject(s.a),Unproject(s.b));}
        Vector3 centre=origin;centre[axis]+=offset;var normal=axis==0?Vector3.right:axis==1?Vector3.up:Vector3.forward;
        Shader.SetGlobalVector("_VRUnitySectionPlane",new Vector4(-avatar.transform.TransformDirection(normal).x,-avatar.transform.TransformDirection(normal).y,-avatar.transform.TransformDirection(normal).z,Vector3.Dot(avatar.transform.TransformDirection(normal),avatar.transform.TransformPoint(centre))));
        Handles.color=new Color(.7f,.8f,1,.3f);Handles.DrawWireDisc(avatar.transform.TransformPoint(centre),avatar.transform.TransformDirection(normal),.25f);
    }
}
