// Generated fixture uses actual production methods/branches. No Unity runtime.
using System;
using UnityEngine;

namespace UnityEngine
{
    public struct Vector3 { }
    public sealed class AudioClip { }
    public sealed class AudioSource
    {
        public AudioClip rawClip;
        public float rawVolume, rawPitch=1f;
        public bool rawPlaying;
        public int reads, writes, plays, stops;
        public AudioClip clip { get { reads++; return rawClip; } set { writes++; rawClip=value; } }
        public float volume { get { reads++; return rawVolume; } set { writes++; rawVolume=value; } }
        public float pitch { get { reads++; return rawPitch; } set { writes++; rawPitch=value; } }
        public bool isPlaying { get { reads++; return rawPlaying; } }
        public void Play() { plays++; rawPlaying=true; }
        public void Stop() { stops++; rawPlaying=false; }
    }
    public static class Mathf
    {
        public static float MoveTowards(float current,float target,float maxDelta)
        { return Math.Abs(target-current)<=maxDelta ? target : current+Math.Sign(target-current)*maxDelta; }
    }
}

internal sealed class Mixer
{
    internal readonly float[] weights={1f,0f};
    internal int writes;
    internal void SetInputWeight(int i,float weight) { weights[i]=weight; writes++; }
}
internal sealed class AnimationFixture
{
    private float visualBlend;
    internal Mixer animations=new Mixer();
    internal void Tick(float wanted,float dt) { UpdateAnimationBlend(wanted,dt); }
    internal void Rebuild() { animations=new Mixer(); visualBlend=0f; }
    /* ANIMATION_METHOD */
}
internal sealed class AudioFixture
{
    internal AudioSource planeLoop=new AudioSource(), windLoop=new AudioSource(), engineLoop=new AudioSource();
    internal bool dropLoopsSettled=true, engineLoopSettled=true;
    internal bool hasDropPhase, wasDriving;
    internal int vehicleGear=1, landingCues;
    internal float previousLand;
    internal Drop drop=new Drop();
    internal Player player=new Player();
    internal sealed class Drop { internal bool HasLanded=true; }
    internal sealed class Player { internal float LastLandingTime=42; }
    private void OneShot(string key,Vector3 pos,float volume) { landingCues++; }
    internal void InactiveDrop(float dt)
    {
        Vector3 pos=default(Vector3);
        /* INACTIVE_DROP */
    }
    internal void InactiveEngine(float dt) { /* INACTIVE_ENGINE */ }
    internal void FlightBegan() { hasDropPhase=true; /* DROP_INVALIDATION */ }
    internal void DrivingBegan() { wasDriving=true; /* ENGINE_INVALIDATION */ }
    internal static bool Apply(AudioSource s,float v,float p,float dt) { return AdjustLoop(s,v,p,dt); }
    internal static void Begin(AudioSource s,AudioClip clip) { BeginLoop(s,clip); }
    /* ADJUST_METHOD */
    /* BEGIN_METHOD */
}
internal static class IdleAudioAnimationTests
{
    private static int checks;
    private static void Check(bool ok,string message) { checks++; if(!ok) throw new Exception(message); }
    private static AudioSource Copy(AudioSource s) => s==null ? null : new AudioSource {
        rawClip=s.rawClip,rawVolume=s.rawVolume,rawPitch=s.rawPitch,rawPlaying=s.rawPlaying };
    // Old behavior: issue native reads every inactive frame, preserving exact
    // MoveTowards fades and the original stop threshold.
    private static void ReferenceFade(AudioSource source,float pitch,float dt)
    {
        if(source==null || source.rawClip==null) return;
        source.rawVolume=Mathf.MoveTowards(source.rawVolume,0f,dt*1.6f);
        source.rawPitch=Mathf.MoveTowards(source.rawPitch,pitch,dt*1.8f);
        if(source.rawVolume<=.001f && source.rawPlaying) source.rawPlaying=false;
    }
    private static void Same(AudioSource actual,AudioSource expected)
    {
        Check(actual.rawClip==expected.rawClip && actual.rawVolume==expected.rawVolume &&
            actual.rawPitch==expected.rawPitch && actual.rawPlaying==expected.rawPlaying,
            "Silent caching changed fade, pitch or playback");
    }
    private static void Audio()
    {
        var initial=new AudioFixture();
        for(int i=0;i<1000;i++) { initial.InactiveDrop(.016f); initial.InactiveEngine(.016f); }
        Check(initial.planeLoop.reads+initial.windLoop.reads+initial.engineLoop.reads==0,
            "Never-started loops made native reads");
        var random=new Random(20261119);
        foreach(int fps in new[]{10,15,30,60,90,120})
            for(int scenario=0;scenario<30;scenario++)
            {
                var f=new AudioFixture();
                f.FlightBegan(); f.DrivingBegan();
                var sources=new[]{f.planeLoop,f.windLoop,f.engineLoop};
                foreach(var s in sources)
                {
                    s.rawClip=new AudioClip(); s.rawVolume=(float)random.NextDouble();
                    s.rawPitch=(float)(.6+random.NextDouble()*1.2); s.rawPlaying=true;
                }
                var expected=new[]{Copy(sources[0]),Copy(sources[1]),Copy(sources[2])};
                for(int i=0;i<3*fps;i++)
                {
                    float dt=i%19==0 ? 0f : 1f/fps;
                    f.InactiveDrop(dt); f.InactiveEngine(dt);
                    ReferenceFade(expected[0],1f,dt); ReferenceFade(expected[1],1f,dt);
                    ReferenceFade(expected[2],.85f,dt);
                    for(int j=0;j<3;j++) Same(sources[j],expected[j]);
                }
                Check(f.dropLoopsSettled && f.engineLoopSettled,"Fade never retired");
                Check(f.landingCues==1 && f.previousLand==42 && !f.wasDriving && f.vehicleGear==1,
                    "Landing/vehicle transition changed");
                foreach(var s in sources) { s.reads=s.writes=0; }
                for(int i=0;i<1200;i++) { f.InactiveDrop(1f/fps); f.InactiveEngine(1f/fps); }
                foreach(var s in sources) Check(s.reads==0 && s.writes==0,"Silent loops still touched engine");
                // Real new flight/drive must invalidate silence and resume fading.
                f.FlightBegan(); f.DrivingBegan();
                foreach(var s in sources) { AudioFixture.Begin(s,new AudioClip()); s.rawVolume=.8f; }
                f.InactiveDrop(1f/fps); f.InactiveEngine(1f/fps);
                foreach(var s in sources) Check(s.rawVolume<.8f && s.reads>0,"Reentry kept stale silent cache");
            }
        Check(AudioFixture.Apply(null,0,1,.1f),"Missing source should be quiet");
        Check(AudioFixture.Apply(new AudioSource(),0,1,.1f),"Missing clip should be quiet");
        var tiny=new AudioSource { rawClip=new AudioClip(),rawVolume=.0005f,rawPitch=.6f,rawPlaying=true };
        Check(!AudioFixture.Apply(tiny,0,1,.001f),"Stop threshold prematurely retired pitch fade");
        Check(!tiny.rawPlaying,"Audible-stop threshold changed");
    }
    private static void Animation()
    {
        var f=new AnimationFixture();
        for(int i=0;i<2000;i++) f.Tick(0,1f/60);
        Check(f.animations.writes==0,"Idle mixer was rewritten");
        for(int i=0;i<120;i++) f.Tick(1,1f/60);
        Check(f.animations.weights[0]==0 && f.animations.weights[1]==1,"Run blend did not converge");
        int old=f.animations.writes;
        for(int i=0;i<2000;i++) f.Tick(1,1f/60);
        Check(f.animations.writes==old,"Steady run mixer was rewritten");
        var rng=new Random(20261119); float reference=1;
        for(int i=0;i<20000;i++)
        {
            float wanted=(float)rng.NextDouble(),dt=(float)(rng.NextDouble()*.15);
            reference=Mathf.MoveTowards(reference,wanted,dt*4f);
            f.Tick(wanted,dt);
            Check(f.animations.weights[0]==1f-reference && f.animations.weights[1]==reference,
                "Animation blend differs from original");
        }
        f.Rebuild(); f.Tick(0,.016f);
        Check(f.animations.weights[0]==1 && f.animations.weights[1]==0 && f.animations.writes==0,
            "Rebuilt graph had stale blend");
    }
    public static void Main()
    {
        Audio(); Animation();
        Console.WriteLine("IDLE_AUDIO_ANIMATION_UNIT_PASS checks="+checks);
        Console.WriteLine("SILENT_FRAMES=216000 LOOP_SOURCE_READS=0 LOOP_SOURCE_WRITES=0 after settling");
        Console.WriteLine("STEADY_MIXER_TICKS=4000 EXTRA_WEIGHT_WRITES=0");
        Console.WriteLine("Controlled API fixtures only; no Unity audio output or device FPS proof.");
    }
}
