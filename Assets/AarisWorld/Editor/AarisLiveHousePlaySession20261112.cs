#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Aaris.MapReview;

[InitializeOnLoad]
internal static class AarisLiveHousePlaySession20261112
{
    const string Request = "/tmp/aaris-house-live-play.request";
    const string Status = "/tmp/aaris-house-live-play.status";
    const string Key = "aaris.livehouse20261112.state";
    static double deadline;
    static AarisLiveHousePlaySession20261112() { EditorApplication.update += Tick; }
    static void Report(string s) { File.WriteAllText(Status,s+"\n"); Debug.Log("AARIS_LIVE_HOUSE "+s); }
    static void Tick()
    {
        try
        {
            if (File.Exists(Request) && SessionState.GetString(Key,"")=="")
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                File.Delete(Request);
                SessionState.SetString(Key,"waiting");
                deadline = EditorApplication.timeSinceStartup + 180;
                Report("PLAY_REQUESTED");
                if (!EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.isPlaying = true;
                return;
            }
            if (SessionState.GetString(Key,"")!="waiting") return;
            if (!EditorApplication.isPlaying) return;
            if (Time.frameCount < 15) return;
            var player = UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player==null) return;
            var drop = player.GetComponent<AarisOfflineDrop>();
            if (drop==null) return;
            if (drop.enabled) drop.enabled = false; // restores the existing foot motor and character visuals
            var target = new Vector3(410.8f, -11.5f, 227.5f);
            foreach (var t in Terrain.activeTerrains)
            {
                if (t==null || t.terrainData==null) continue;
                var pos=t.transform.position; var size=t.terrainData.size;
                if (target.x<pos.x||target.x>pos.x+size.x||target.z<pos.z||target.z>pos.z+size.z) continue;
                target.y=t.SampleHeight(target)+.26f;
                break;
            }
            var controller=player.GetComponent<CharacterController>();
            if(controller!=null) controller.enabled=false;
            player.transform.SetPositionAndRotation(target,Quaternion.Euler(0,0,0));
            if(controller!=null) controller.enabled=true;
            player.ResumeFromAerialDrop();
            Physics.SyncTransforms();
            var gameViewType=typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if(gameViewType!=null)
            {
                var view=EditorWindow.GetWindow(gameViewType);
                view.Show(); view.Focus(); view.maximized=true;
            }
            SessionState.SetString(Key,"");
            Report("PLAY_LIVE_AT_CLOCKTOWERTOWN_BUNGALOW pos="+target.ToString("F2"));
        }
        catch(Exception e)
        {
            SessionState.SetString(Key,"");
            Report("ERROR "+e.GetType().Name+" "+e.Message);
        }
    }
}
#endif
