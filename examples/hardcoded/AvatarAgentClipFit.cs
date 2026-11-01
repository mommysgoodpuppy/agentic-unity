// EXAMPLE, HARDCODED FOR ONE AVATAR: scene paths, object names and measurements below are that avatar's,
// so this won't run on yours as it is. It's an example of a kind of tool: see examples/hardcoded/README.md.
// No assets are included; any vendor asset it names is a path in a project that owns a copy.
// Local, generated-draft clipping audit and fitting. Original assets are never edited.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

public static class AvatarAgentClipFit
{
    [Serializable] public class Shape { public string name; public float weight; public Vector3[] posed; }
    [Serializable] public class MaterialInfo { public string path, shader; public string[] properties; }
    [Serializable] public class MeshInfo
    {
        public string path, mesh; public Vector3 position, scale; public Vector3[] vertices; public int[] triangles, submeshCounts;
        public MaterialInfo[] materials; public Shape[] shapes; public string[] bones;
    }
    [Serializable] public class Report { public MeshInfo[] meshes; }
    static string Arg(string key) { var a=Environment.GetCommandLineArgs(); int i=Array.IndexOf(a,key); return i<0?null:a[i+1]; }
    static string PathOf(Transform t,Transform root)=>AnimationUtility.CalculateTransformPath(t,root);
    [Serializable] public class FitConfig
    {
        public string source, scene, assetDir, report;
        public float leftFootScale=.90f, rightFootScale=.90f, sockThicc=100, leftCalfScale=.90f, collarRadialScale=1.12f, collarLift=.002f;
    }
    static void AddCorrection(SkinnedMeshRenderer r, string name, Func<int,Vector3,Vector3> correction)
    {
        var mesh=r.sharedMesh;
        if(mesh.GetBlendShapeIndex(name)>=0)throw new InvalidOperationException("Correction already exists: "+name);
        var world=AvatarMeshGeometry.WorldVertices(r);var d=new Vector3[mesh.vertexCount];
        var bones=r.bones;var poses=mesh.bindposes;var weights=mesh.boneWeights;
        var matrices=bones.Select((b,i)=>b?b.localToWorldMatrix*poses[i]:Matrix4x4.identity).ToArray();
        float maximum=0;
        for(int i=0;i<d.Length;i++)
        {
            var delta=correction(i,world[i]);maximum=Mathf.Max(maximum,delta.magnitude);
            if(delta.sqrMagnitude<1e-20f)continue;
            var w=weights[i];var skin=new Matrix4x4();
            for(int k=0;k<16;k++)skin[k]=matrices[w.boneIndex0][k]*w.weight0+matrices[w.boneIndex1][k]*w.weight1+matrices[w.boneIndex2][k]*w.weight2+matrices[w.boneIndex3][k]*w.weight3;
            d[i]=skin.inverse.MultiplyVector(delta);
        }
        mesh.AddBlendShapeFrame(name,100,d,new Vector3[d.Length],new Vector3[d.Length]);
        r.SetBlendShapeWeight(mesh.GetBlendShapeIndex(name),100);
        Debug.Log($"[AvatarAgentClipFit] {name}: maximum correction {maximum*1000:F2} mm");
    }
    static Mesh CopyMesh(SkinnedMeshRenderer r,string path)
    {
        if(File.Exists(path))throw new InvalidOperationException("Asset exists: "+path);
        var old=r.sharedMesh;var weights=Enumerable.Range(0,old.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray();
        var mesh=UnityEngine.Object.Instantiate(old);mesh.name=old.name+" (clipping fit)";
        AssetDatabase.CreateAsset(mesh,path);r.sharedMesh=mesh;
        for(int i=0;i<weights.Length;i++)r.SetBlendShapeWeight(i,weights[i]);
        return mesh;
    }
    public static void Apply()
    {
        try
        {
            var c=JsonUtility.FromJson<FitConfig>(File.ReadAllText(Arg("-config")));
            if(!c.scene.StartsWith("Assets/VRUnityGenerated/") || !c.assetDir.StartsWith("Assets/VRUnityGenerated/") || File.Exists(c.scene))throw new ArgumentException("Choose a new generated draft");
            if(c.leftFootScale<.8f || c.leftFootScale>1 || c.rightFootScale<.8f || c.rightFootScale>1 || c.leftCalfScale<.8f || c.leftCalfScale>1 || c.sockThicc<0 || c.sockThicc>100 || c.collarRadialScale<1 || c.collarRadialScale>1.3f || Mathf.Abs(c.collarLift)>.01f)throw new ArgumentException("Fitting values outside supported range");
            var scene=EditorSceneManager.OpenScene(c.source,OpenSceneMode.Single);
            EditorSceneManager.SaveScene(scene,c.scene,true);scene=EditorSceneManager.OpenScene(c.scene,OpenSceneMode.Single);
            var avatar=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;var root=avatar.transform;
            Directory.CreateDirectory(c.assetDir);AssetDatabase.Refresh();
            var body=root.Find("Sotai").GetComponent<SkinnedMeshRenderer>();CopyMesh(body,c.assetDir+"/Body-FootFit.asset");
            var animator=avatar.GetComponent<Animator>();
            foreach(var side in new[]{(bone:HumanBodyBones.LeftFoot,scale:c.leftFootScale,name:"VRUnity_LeftFootFit"),(bone:HumanBodyBones.RightFoot,scale:c.rightFootScale,name:"VRUnity_RightFootFit")})
            {
                var foot=animator.GetBoneTransform(side.bone);var bones=body.bones;var weights=body.sharedMesh.boneWeights;
                float Influence(int index){var w=weights[index];float sum=0;var bi=new[]{w.boneIndex0,w.boneIndex1,w.boneIndex2,w.boneIndex3};var bw=new[]{w.weight0,w.weight1,w.weight2,w.weight3};for(int k=0;k<4;k++)if(bones[bi[k]] && (bones[bi[k]]==foot || bones[bi[k]].IsChildOf(foot)))sum+=bw[k];return sum;}
                AddCorrection(body,side.name,(i,p)=>(p-foot.position)*((side.scale-1)*Influence(i)));
            }
            if(c.leftCalfScale<1)
            {
                var low=animator.GetBoneTransform(HumanBodyBones.LeftFoot).position;
                var high=animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position;
                AddCorrection(body,"VRUnity_LeftSockClearance",(i,p)=>{
                    var local=root.InverseTransformPoint(p);if(local.x>=0)return Vector3.zero;
                    float envelope=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.07f,.105f,local.y))*Mathf.SmoothStep(0,1,Mathf.InverseLerp(.53f,.45f,local.y));
                    float t=Mathf.Clamp01(Vector3.Dot(p-low,high-low)/(high-low).sqrMagnitude);var axis=Vector3.Lerp(low,high,t);
                    var radial=p-axis;radial.y=0;
                    return radial*((c.leftCalfScale-1)*envelope);
                });
            }
            var sock=root.Find("Velle Left Stirrup").GetComponent<SkinnedMeshRenderer>();int thicc=sock.sharedMesh.GetBlendShapeIndex("Thicc");if(thicc<0)throw new InvalidOperationException("Sock Thicc missing");sock.SetBlendShapeWeight(thicc,c.sockThicc);
            var collar=root.Find("Collar (Dog Bone) Variant/Collar_mesh").GetComponent<SkinnedMeshRenderer>();CopyMesh(collar,c.assetDir+"/Collar-Clearance.asset");
            var neck=animator.GetBoneTransform(HumanBodyBones.Neck);var centre=neck.position;
            AddCorrection(collar,"VRUnity_NeckClearance",(i,p)=>{var d=p-centre;return new Vector3(d.x*(c.collarRadialScale-1),c.collarLift,d.z*(c.collarRadialScale-1));});
            var hair=root.Find("Velle Optim2026hair_prefab/Hair_Back_Short").GetComponent<SkinnedMeshRenderer>();var mats=hair.sharedMaterials;
            if(mats.Length!=7 || mats[3].shader.name!="Standard")throw new InvalidOperationException("Unexpected hair material setup");mats[3]=mats[0];hair.sharedMaterials=mats;
            EditorUtility.SetDirty(body.sharedMesh);EditorUtility.SetDirty(collar.sharedMesh);AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);if(!EditorSceneManager.SaveScene(scene))throw new IOException("Save failed");
            File.WriteAllText(c.report,JsonUtility.ToJson(c,true));Debug.Log("[AvatarAgentClipFit] DONE");EditorApplication.Exit(0);
        }catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
    }
    public static void Inspect()
    {
        try
        {
            var scene=EditorSceneManager.OpenScene(Arg("-scene"),OpenSceneMode.Single);
            var avatar=scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<VRCAvatarDescriptor>(true)).Single().gameObject;
            var root=avatar.transform;
            var paths=new[]{"Sotai","Velle Left Stirrup","Soraneco Witch Outfit/Shoose","Soraneco Witch Outfit/Pants","Collar (Dog Bone) Variant/Collar_mesh","Velle Optim2026hair_prefab/Hair_Back_Short"};
            var report=new Report { meshes=paths.Select(path=>{
                var r=root.Find(path).GetComponent<SkinnedMeshRenderer>(); var m=r.sharedMesh;
                Vector3[] Pose()=>AvatarMeshGeometry.WorldVertices(r).Select(v=>root.InverseTransformPoint(v)).ToArray();
                var shapes=Enumerable.Range(0,m.blendShapeCount).Select(i=>{
                    string name=m.GetBlendShapeName(i); float w=r.GetBlendShapeWeight(i); Vector3[] posed=null;
                    bool relevant=path!="Sotai" || name.IndexOf("Foot",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("Toes",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("Heel",StringComparison.OrdinalIgnoreCase)>=0 || name.IndexOf("Leg",StringComparison.OrdinalIgnoreCase)>=0 || name=="Pants_Clip";
                    if(relevant){r.SetBlendShapeWeight(i,100);posed=Pose();r.SetBlendShapeWeight(i,w);}
                    return new Shape{name=name,weight=w,posed=posed};
                }).ToArray();
                return new MeshInfo {path=path,mesh=AssetDatabase.GetAssetPath(m),position=root.InverseTransformPoint(r.transform.position),scale=r.transform.localScale,
                    vertices=Pose(),triangles=m.triangles,submeshCounts=Enumerable.Range(0,m.subMeshCount).Select(i=>m.GetTriangles(i).Length/3).ToArray(),shapes=shapes,bones=r.bones.Select(b=>b?PathOf(b,root):"").ToArray(),
                    materials=r.sharedMaterials.Select(mat=>new MaterialInfo{path=AssetDatabase.GetAssetPath(mat),shader=mat?mat.shader.name:"",properties=mat?Enumerable.Range(0,ShaderUtil.GetPropertyCount(mat.shader)).Select(i=>{
                        var n=ShaderUtil.GetPropertyName(mat.shader,i);var type=ShaderUtil.GetPropertyType(mat.shader,i);
                        if(type==ShaderUtil.ShaderPropertyType.Color)return n+"="+mat.GetColor(n);
                        if(type==ShaderUtil.ShaderPropertyType.TexEnv)return n+"="+AssetDatabase.GetAssetPath(mat.GetTexture(n));
                        if(type==ShaderUtil.ShaderPropertyType.Float || type==ShaderUtil.ShaderPropertyType.Range)return n+"="+mat.GetFloat(n);
                        return null;
                    }).Where(s=>s!=null).ToArray():new string[0]}).ToArray()};
            }).ToArray()};
            File.WriteAllText(Arg("-output"),JsonUtility.ToJson(report));
            Debug.Log("[AvatarAgentClipFit] DONE");EditorApplication.Exit(0);
        }catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}
    }
}
