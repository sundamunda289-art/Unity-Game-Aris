using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    [InitializeOnLoad]
    internal static class AarisKenneyOpenHousePhysicsTest
    {
        const string Request="/tmp/aaris-codespaces/kenney-physics.request";
        const string Report="/tmp/aaris-codespaces/kenney-physics.report";
        static AarisKenneyOpenHousePhysicsTest(){EditorApplication.delayCall+=Tick;}
        static void Tick()
        {
            if(!File.Exists(Request))return;
            if(EditorApplication.isCompiling||EditorApplication.isPlayingOrWillChangePlaymode)return;
            File.Delete(Request);Run();
        }
        [MenuItem("Aaris/Assets/Test Kenney Open House Physics")]
        public static void Run()
        {
            StringBuilder sb=new StringBuilder();
            bool overall=true;
            string[] names={"OpenCottage","OpenBungalow","OpenDetached"};
            float[] widths={9.968f,14.448f,12.383f};
            float[] depths={14.349f,14.349f,16.430f};
            float[] heights={7.517f,6.673f,10.244f};
            for(int i=0;i<names.Length;i++)
            {
                GameObject prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/AarisBuildings/"+names[i]+".prefab");
                if(prefab==null){sb.AppendLine(names[i]+":MISSING_PREFAB");overall=false;continue;}
                GameObject obj=PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if(obj==null){sb.AppendLine(names[i]+":INSTANTIATE_FAILED");overall=false;continue;}
                try
                {
                    obj.transform.position=new Vector3(0,10000+i*100,0);
                    Physics.SyncTransforms();
                    MeshCollider collider=obj.GetComponentInChildren<MeshCollider>();
                    MeshFilter filter=obj.GetComponentInChildren<MeshFilter>();
                    if(collider==null||filter==null||filter.sharedMesh==null)
                        throw new Exception("MISSING_MESH_OR_COLLIDER");
                    collider.sharedMesh=null; // recook imported offline mesh
                    collider.sharedMesh=filter.sharedMesh;
                    Physics.SyncTransforms();
                    float w=widths[i],d=depths[i],h=heights[i];
                    float upperY=10000+i*100;
                    Vector3 origin=new Vector3(0,upperY,0);
                    bool door=collider.Raycast(new Ray(origin+new Vector3(0,1.2f,-d*.5f-2),Vector3.forward),out RaycastHit doorHit,3.5f);
                    bool leftWindow=collider.Raycast(new Ray(origin+new Vector3(-w/5f,1.5f,-d*.5f-2),Vector3.forward),out _,3.5f);
                    bool rightWindow=collider.Raycast(new Ray(origin+new Vector3(w/5f,1.5f,-d*.5f-2),Vector3.forward),out _,3.5f);
                    bool wall=collider.Raycast(new Ray(origin+new Vector3(2*w/5f,1.2f,-d*.5f-2),Vector3.forward),out _,3.5f);
                    bool roof=collider.Raycast(new Ray(origin+new Vector3(0,h+2,0),Vector3.down),out _,3.5f);
                    int tris=filter.sharedMesh.triangles.Length/3;
                    string triVertices="none";
                    if(door && doorHit.triangleIndex>=0 && doorHit.triangleIndex<tris)
                    {
                       int idx=doorHit.triangleIndex*3;
                       var mesh=filter.sharedMesh;
                       var triangles=mesh.triangles; var vertices=mesh.vertices;
                       var a=filter.transform.TransformPoint(vertices[triangles[idx]])-origin;
                       var b=filter.transform.TransformPoint(vertices[triangles[idx+1]])-origin;
                       var c=filter.transform.TransformPoint(vertices[triangles[idx+2]])-origin;
                       triVertices=a.ToString("F2")+"|"+b.ToString("F2")+"|"+c.ToString("F2");
                    }
                    bool ok=!door && !leftWindow && !rightWindow && wall && roof && tris<=5000;
                    sb.AppendLine(names[i]+" pass="+ok+" doorBlocked="+door+" windowBlocked="+leftWindow+"/"+rightWindow+" wallSolid="+wall+" roofSolid="+roof+" tris="+tris+" hit="+(door?doorHit.point.ToString("F3"):"none")+" hitTri="+(door?doorHit.triangleIndex:-1)+" triVertices="+triVertices);
                    overall &= ok;
                }
                catch(Exception e){sb.AppendLine(names[i]+":ERROR_"+e.GetType().Name);overall=false;}
                finally{UnityEngine.Object.DestroyImmediate(obj);}
            }
            File.WriteAllText(Report,"RESULT="+(overall?"PASS":"FAIL")+"\n"+sb.ToString());
            Debug.Log(overall?"AARIS_KENNEY_PHYSICS_PASS":"AARIS_KENNEY_PHYSICS_FAIL");
        }
    }
}

// Revalidate physical apertures after offline mesh rebuild.

// Run after quarter-turn fix.
