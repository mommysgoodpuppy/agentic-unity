// EXAMPLE, HARDCODED FOR ONE AVATAR: scene paths, object names and measurements below are that avatar's,
// so this won't run on yours as it is. It's an example of a kind of tool: see examples/hardcoded/README.md.
// No assets are included; any vendor asset it names is a path in a project that owns a copy.
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class AvatarAgentSock
{
    [Serializable] class Dump { public Vector3[] vertices,flat,thigh; public Vector2[] uv; public int[] triangles; public string[] bones; public BoneWeight[] weights; }
    static string Work=>Path.GetFullPath(Path.Combine(Application.dataPath,"../Library/VRUnityAgent/Sock"));
    static AvatarAgentSock(){EditorApplication.update+=Poll;}
    static void Poll()
    {
        if(EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlaying)return;
        var request=Path.Combine(Work,"request.txt");if(!File.Exists(request))return;
        string action=File.ReadAllText(request).Trim();File.Delete(request);
        try{if(action=="inspect")Inspect(); else if(action=="velle-stirrup")AttachVelleStirrup();else if(action=="export-stirrup")ExportStirrup();else if(action=="apply-stars")ApplyStars();}catch(Exception e){File.WriteAllText(Path.Combine(Work,"error.txt"),e.ToString());}
    }
    static void AttachVelleStirrup()
    {
        var avatar=AvatarAgentCrossSection.Avatar();
        if(avatar.scene.path!="Assets/VRUnityGenerated/Velle-SoranecoWitch.unity")throw new Exception("Wrong draft");
        if(avatar.transform.Find("Velle Left Stirrup"))throw new Exception("Left stirrup already attached");
        var source=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/MisterPink/Velle/FBX/Velle.fbx");
        var skin=source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r=>r.name=="Clothing_Stirrups_L");
        var material=AssetDatabase.LoadAssetAtPath<Material>("Assets/MisterPink/Velle/Texture/Materials/liltoon/Clothing_Stirrup.mat");
        if(!skin.sharedMesh||!material||!material.shader)throw new Exception("Source stirrup dependency missing");
        var targetArmature=avatar.transform.Find("Armature");var targets=targetArmature.GetComponentsInChildren<Transform>(true);
        var used=new System.Collections.Generic.HashSet<int>();using(var weights=skin.sharedMesh.GetAllBoneWeights())foreach(var w in weights)if(w.weight>0)used.Add(w.boneIndex);
        var sourceArmature=source.transform.Find("Armature");
        Transform Match(Transform bone)
        {
            if(bone==sourceArmature)return targetArmature;
            var path=AnimationUtility.CalculateTransformPath(bone,sourceArmature);var target=targetArmature.Find(path);
            if(!target)throw new Exception("Missing primary-rig stirrup bone: "+path);
            return target;
        }
        var mapped=skin.bones.Select((b,i)=>used.Contains(i)?Match(b):targetArmature).ToArray();
        var root=skin.rootBone?Match(skin.rootBone):targetArmature;
        var go=new GameObject("Velle Left Stirrup");
        try
        {
            go.transform.SetParent(avatar.transform,false);
            var matrix=source.transform.worldToLocalMatrix*skin.transform.localToWorldMatrix;
            go.transform.localPosition=matrix.GetColumn(3);go.transform.localRotation=matrix.rotation;go.transform.localScale=matrix.lossyScale;
            var result=go.AddComponent<SkinnedMeshRenderer>();EditorUtility.CopySerialized(skin,result);
            result.bones=mapped;result.rootBone=root;result.sharedMaterials=Enumerable.Repeat(material,result.sharedMesh.subMeshCount).ToArray();result.enabled=true;go.SetActive(true);
            var points=AvatarMeshGeometry.WorldVertices(result);
            var bounds=new Bounds(root.InverseTransformPoint(points[0]),Vector3.zero);foreach(var point in points)bounds.Encapsulate(root.InverseTransformPoint(point));bounds.Expand(.1f);result.localBounds=bounds;
            if(points.Any(v=>float.IsNaN(v.x)||float.IsNaN(v.y)||float.IsNaN(v.z)))throw new Exception("Invalid stirrup geometry");
            Undo.RegisterCreatedObjectUndo(go,"Restore Velle left stirrup");UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.scene);
            File.WriteAllText(Path.Combine(Work,"velle-stirrup.txt"),"Attached Velle's original Clothing_Stirrups_L mesh and lilToon material; "+used.Count+" weighted bones matched by exact primary-armature paths; "+points.Length+" vertices. Existing Velle body unchanged.");
            Selection.activeGameObject=go;EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();
        }
        catch{UnityEngine.Object.DestroyImmediate(go);throw;}
    }
    static void ExportStirrup()
    {
        var avatar=AvatarAgentCrossSection.Avatar();var skin=avatar.transform.Find("Velle Left Stirrup").GetComponent<SkinnedMeshRenderer>();
        var points=AvatarMeshGeometry.WorldVertices(skin).Select(v=>avatar.transform.InverseTransformPoint(v)).ToArray();
        var d=new Dump{vertices=points,uv=skin.sharedMesh.uv,triangles=skin.sharedMesh.triangles};
        File.WriteAllText(Path.Combine(Work,"stirrup-uv.json"),JsonUtility.ToJson(d));
    }
    static void ApplyStars()
    {
        var avatar=AvatarAgentCrossSection.Avatar();if(avatar.scene.path!="Assets/VRUnityGenerated/Velle-SoranecoWitch.unity")throw new Exception("Wrong scene");
        var skin=avatar.transform.Find("Velle Left Stirrup").GetComponent<SkinnedMeshRenderer>();
        string folder="Assets/VRUnityGenerated/SoranecoWitch/StirrupStars";
        string path=folder+"/Velle Stirrup - Soraneco Stars.mat";
        if(File.Exists(path))throw new Exception("Star material already exists");
        var texture=AssetDatabase.LoadAssetAtPath<Texture2D>(folder+"/SoranecoStarsAtlas.png");if(!texture)throw new Exception("Missing star atlas");
        var importer=(TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture));importer.alphaIsTransparency=true;importer.wrapMode=TextureWrapMode.Clamp;importer.SaveAndReimport();
        var material=new Material(skin.sharedMaterial);material.name="Velle Stirrup - Soraneco Stars";
        material.SetFloat("_UseMain2ndTex",1);material.SetTexture("_Main2ndTex",texture);material.SetColor("_Color2nd",Color.white);
        material.SetTextureScale("_Main2ndTex",Vector2.one);material.SetTextureOffset("_Main2ndTex",Vector2.zero);
        material.SetFloat("_Main2ndTexAngle",0);material.SetFloat("_Main2ndTex_UVMode",0);
        material.SetFloat("_Main2ndTexIsDecal",0);material.SetFloat("_Main2ndTexBlendMode",0);material.SetFloat("_Main2ndEnableLighting",1);
        AssetDatabase.CreateAsset(material,path);
        Undo.RecordObject(skin,"Add Soraneco purple stars decal to Velle stirrup");skin.sharedMaterials=Enumerable.Repeat(material,skin.sharedMesh.subMeshCount).ToArray();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(avatar.scene);EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();
        File.WriteAllText(Path.Combine(Work,"stars-applied.txt"),"Original Soraneco stars UV-baked onto a transparent secondary colour layer on an isolated copy of Velle's stirrup material. Base texture/mesh unchanged.");
    }
    static void Inspect()
    {
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/AtelierAlca/Soraneco/Prefab/Soraneco.prefab");
        var instance=UnityEngine.Object.Instantiate(prefab);
        try
        {
            instance.transform.position=Vector3.zero;instance.transform.rotation=Quaternion.identity;
            var skin=instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r=>r.name=="Body");
            int thigh=skin.sharedMesh.GetBlendShapeIndex("bs.ThighHigh_L");
            skin.SetBlendShapeWeight(thigh,0);var flat=AvatarMeshGeometry.WorldVertices(skin);
            skin.SetBlendShapeWeight(thigh,100);var full=AvatarMeshGeometry.WorldVertices(skin);
            var d=new Dump{vertices=skin.sharedMesh.vertices,flat=flat,thigh=full,uv=skin.sharedMesh.uv,triangles=skin.sharedMesh.triangles,bones=skin.bones.Select(b=>b?b.name:"").ToArray(),weights=skin.sharedMesh.boneWeights};
            File.WriteAllText(Path.Combine(Work,"source.json"),JsonUtility.ToJson(d));
        }
        finally{UnityEngine.Object.DestroyImmediate(instance);}
    }
}
