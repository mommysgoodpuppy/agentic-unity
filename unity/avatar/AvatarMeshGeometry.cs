// World-space CPU skinning independent of SkinnedMeshRenderer.BakeMesh scale conventions.
using System;
using System.Linq;
using UnityEngine;

public static class AvatarMeshGeometry
{
    public static Vector3[] WorldVertices(SkinnedMeshRenderer renderer)
    {
        var mesh=renderer.sharedMesh;if(!mesh)throw new ArgumentException("No mesh");
        var vertices=mesh.vertices;
        for(int shape=0;shape<mesh.blendShapeCount;shape++)
        {
            float weight=renderer.GetBlendShapeWeight(shape);if(Mathf.Abs(weight)<1e-7f)continue;
            int count=mesh.GetBlendShapeFrameCount(shape);if(count==0)continue;
            var times=Enumerable.Range(0,count).Select(i=>mesh.GetBlendShapeFrameWeight(shape,i)).ToArray();
            int lo=-1,hi=0;
            if(weight<times[0]&&times[0]<0){lo=0;hi=count>1?1:-1;}
            else
            {
                for(int i=0;i<count;i++)if(times[i]<=weight)lo=i;
                hi=lo+1;
                if(hi>=count){hi=count-1;lo=count>1?count-2:-1;}
            }
            float wl=lo<0?0:times[lo],wh=hi<0?0:times[hi];float fraction=Mathf.Approximately(wl,wh)?0:(weight-wl)/(wh-wl);
            var dl=new Vector3[vertices.Length];var dh=new Vector3[vertices.Length];
            if(lo>=0)mesh.GetBlendShapeFrameVertices(shape,lo,dl,null,null);
            if(hi>=0)mesh.GetBlendShapeFrameVertices(shape,hi,dh,null,null);
            for(int i=0;i<vertices.Length;i++)vertices[i]+=Vector3.LerpUnclamped(dl[i],dh[i],fraction);
        }
        var bones=renderer.bones;var binds=mesh.bindposes;
        var matrices=bones.Select((b,i)=>b&&i<binds.Length?b.localToWorldMatrix*binds[i]:Matrix4x4.zero).ToArray();
        var world=new Vector3[vertices.Length];int index=0;
        using(var counts=mesh.GetBonesPerVertex())using(var weights=mesh.GetAllBoneWeights())
        {
            for(int vertex=0;vertex<vertices.Length;vertex++)
            {
                if(counts[vertex]==0){world[vertex]=renderer.transform.TransformPoint(vertices[vertex]);continue;}
                Vector3 position=Vector3.zero;
                for(int k=0;k<counts[vertex];k++)
                {
                    var w=weights[index++];if(w.weight==0)continue;
                    if(w.boneIndex>=bones.Length||!bones[w.boneIndex])throw new Exception("Missing weighted bone on "+renderer.name);
                    position+=matrices[w.boneIndex].MultiplyPoint3x4(vertices[vertex])*w.weight;
                }
                world[vertex]=position;
            }
        }
        return world;
    }
}
