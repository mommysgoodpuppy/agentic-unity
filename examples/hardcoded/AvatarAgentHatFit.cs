// EXAMPLE, HARDCODED FOR ONE AVATAR: scene paths, object names and measurements below are that avatar's,
// so this won't run on yours as it is. It's an example of a kind of tool: see examples/hardcoded/README.md.
// No assets are included; any vendor asset it names is a path in a project that owns a copy.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

public static class AvatarAgentHatFit
{
    [Serializable] class Rest { public Vector3 position,scale; public Quaternion rotation; }
    static GameObject Avatar()
    {
        var avatar=AvatarAgentCrossSection.Avatar();
        if(avatar.scene.path!="Assets/VRUnityGenerated/Velle-SoranecoWitch.unity")throw new Exception("Hat edits require the witch draft");
        return avatar;
    }
    static Vector3[] Vertices(SkinnedMeshRenderer renderer)
    {
        var mesh=new Mesh();try{renderer.BakeMesh(mesh);return mesh.vertices.Select(v=>renderer.transform.TransformPoint(v)).ToArray();}
        finally{UnityEngine.Object.DestroyImmediate(mesh);}
    }
    public static void Prepare()
    {
        var avatar=Avatar();if(avatar.transform.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="Witch Hat"))throw new Exception("Hat anchor exists");
        var source=avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r=>r.name=="Hat"&&r.gameObject.activeInHierarchy);
        var before=Vertices(source);var used=new HashSet<int>();
        using(var weights=source.sharedMesh.GetAllBoneWeights())foreach(var w in weights)if(w.weight>0)used.Add(w.boneIndex);
        var head=source.bones.Single(b=>b&&b.name=="Head");
        var destination=avatar.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
        var keep=new HashSet<Transform>{head};
        foreach(int i in used)
        {
            var bone=source.bones[i];if(!bone||!bone.IsChildOf(head))throw new Exception("Hat weighted outside source Head");
            for(var t=bone;t!=head;t=t.parent)keep.Add(t);
        }
        var physics=head.GetComponentsInChildren<VRCPhysBone>(true).Where(p=>keep.Contains(p.transform)).ToArray();
        foreach(var p in physics)foreach(var t in p.GetComponentsInChildren<Transform>(true))keep.Add(t);
        const string checkpoint="Assets/VRUnityGenerated/Checkpoints/Before-HatFit-2026-10-26.unity";
        if(!File.Exists(checkpoint))EditorSceneManager.SaveScene(avatar.scene,checkpoint,true);
        Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("Make witch hat independently adjustable");
        GameObject container=null;
        try
        {
            container=new GameObject("Witch Hat");container.transform.SetParent(destination,false);
            var relative=destination.worldToLocalMatrix*head.localToWorldMatrix;
            container.transform.localPosition=relative.GetColumn(3);container.transform.localRotation=relative.rotation;container.transform.localScale=relative.lossyScale;
            var skeleton=UnityEngine.Object.Instantiate(head.gameObject,container.transform,false);
            if(PrefabUtility.IsPartOfPrefabInstance(skeleton))PrefabUtility.UnpackPrefabInstance(skeleton,PrefabUnpackMode.Completely,InteractionMode.AutomatedAction);
            skeleton.name="Hat Rig";skeleton.transform.localPosition=Vector3.zero;skeleton.transform.localRotation=Quaternion.identity;skeleton.transform.localScale=Vector3.one;
            var map=keep.ToDictionary(t=>t,t=>t==head?skeleton.transform:skeleton.transform.Find(AnimationUtility.CalculateTransformPath(t,head)));
            if(map.Values.Any(t=>!t))throw new Exception("Hat rig copy could not be mapped");
            var kept=new HashSet<Transform>(map.Values);
            foreach(var c in skeleton.GetComponentsInChildren<Component>(true))if(c&&!(c is Transform)&&!c.GetType().FullName.StartsWith("VRC.SDK3.Dynamics.PhysBone.",StringComparison.Ordinal))UnityEngine.Object.DestroyImmediate(c);
            foreach(var t in skeleton.GetComponentsInChildren<Transform>(true).Reverse())if(t&&!kept.Contains(t))UnityEngine.Object.DestroyImmediate(t.gameObject);
            var go=new GameObject("Hat");go.transform.SetParent(container.transform,false);
            var matrix=container.transform.worldToLocalMatrix*source.transform.localToWorldMatrix;
            go.transform.localPosition=matrix.GetColumn(3);go.transform.localRotation=matrix.rotation;go.transform.localScale=matrix.lossyScale;
            var renderer=go.AddComponent<SkinnedMeshRenderer>();EditorUtility.CopySerialized(source,renderer);
            renderer.bones=source.bones.Select((b,i)=>used.Contains(i)?map[b]:skeleton.transform).ToArray();renderer.rootBone=skeleton.transform;
            var after=Vertices(renderer);float error=before.Select((v,i)=>Vector3.Distance(v,after[i])).Max();
            if(error>.00001f)throw new Exception("Hat separation changed geometry: "+error+" m");
            var rootPoints=after.Select(p=>skeleton.transform.InverseTransformPoint(p)).ToArray();float radius=rootPoints.Max(p=>p.magnitude)+.15f;
            renderer.localBounds=new Bounds(Vector3.zero,Vector3.one*radius*2);renderer.updateWhenOffscreen=false;
            foreach(var p in container.GetComponentsInChildren<VRCPhysBone>(true))
            {
                if(p.rootTransform&&!p.rootTransform.IsChildOf(container.transform))throw new Exception("External hat physics root");
                if(p.colliders.Any(c=>c&&!c.transform.IsChildOf(container.transform)))throw new Exception("External hat physics collider");
            }
            Undo.RegisterCreatedObjectUndo(container,"Create fitted hat anchor");
            Undo.RecordObject(source.gameObject,"Disable old merged hat renderer");source.gameObject.SetActive(false);
            foreach(var p in physics)Undo.DestroyObjectImmediate(p);
            EditorSceneManager.MarkSceneDirty(avatar.scene);Undo.CollapseUndoOperations(group);
            var rest=new Rest{position=container.transform.localPosition,rotation=container.transform.localRotation,scale=container.transform.localScale};
            File.WriteAllText(Path.Combine(AvatarAgentCrossSection.Work,"hat-rest.json"),JsonUtility.ToJson(rest));
            File.WriteAllText(Path.Combine(AvatarAgentCrossSection.Work,"hat-prepared.txt"),"Baked geometry preserved within "+error+" m; "+used.Count+" weighted bones; "+physics.Length+" PhysBones retained on independent rig. Original renderer disabled; old duplicate hat physics removed.");
            Selection.activeGameObject=container;EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();
        }
        catch{Undo.RevertAllDownToGroup(group);if(container)UnityEngine.Object.DestroyImmediate(container);throw;}
    }
    public static void Position(Vector3 offset,Vector3 euler,float size,float depth)
    {
        var avatar=Avatar();var anchor=avatar.GetComponentsInChildren<Transform>(true).Single(t=>t.name=="Witch Hat");
        var rest=JsonUtility.FromJson<Rest>(File.ReadAllText(Path.Combine(AvatarAgentCrossSection.Work,"hat-rest.json")));
        if(offset.magnitude>.2f||euler.magnitude>45||size<.75f||size>1.25f||depth<.75f||depth>1.25f)throw new Exception("Hat fitting adjustment exceeds 20 cm / 45 degrees");
        Undo.RecordObject(anchor,"Position witch hat from mesh sections");
        anchor.localPosition=rest.position;anchor.localRotation=rest.rotation;anchor.localScale=rest.scale;
        var pivot=anchor.parent.position;
        var rotation=avatar.transform.rotation*Quaternion.Euler(euler)*Quaternion.Inverse(avatar.transform.rotation);
        anchor.position=pivot+rotation*((anchor.position-pivot)*size)+avatar.transform.TransformVector(offset);anchor.rotation=rotation*anchor.rotation;anchor.localScale=Vector3.Scale(rest.scale*size,new Vector3(1,1,depth));
        EditorSceneManager.MarkSceneDirty(avatar.scene);EditorApplication.QueuePlayerLoopUpdate();SceneView.RepaintAll();Selection.activeTransform=anchor;
        File.WriteAllText(Path.Combine(AvatarAgentCrossSection.Work,"hat-position.txt"),"Offset in avatar space: "+offset+"; rotation about Head in avatar axes: "+euler+"; depth multiplier: "+depth+"; uniform scale: "+size+". Reversible Unity Undo step.");
    }
}
