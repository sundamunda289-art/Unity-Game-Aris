using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object=UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    // Native Unity photos of the real authored assets and production finish
    // factories under a fixed, reproducible neutral daylight studio.
    public static class AarisMobileSubjectVisualQA
    {
        private const BindingFlags Priv=BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static string dir;
        private static Camera camera;
        private static readonly List<string> report=new List<string>();
        private static void Require(bool ok,string label)
        {
            report.Add((ok?"PASS ":"FAIL ")+label);
            if(!ok) throw new InvalidOperationException(label);
        }
        private static Bounds BoundsOf(GameObject root)
        {
            var rs=root.GetComponentsInChildren<Renderer>(true);
            bool any=false;Bounds b=default;
            foreach(var r in rs)
            {
                if(!r.enabled || !r.gameObject.activeInHierarchy || r is LineRenderer) continue;
                if(r.bounds.size.sqrMagnitude<.000001f)continue;
                if(!any){b=r.bounds;any=true;}else b.Encapsulate(r.bounds);
            }
            Require(any,root.name+" has native renderer bounds");
            return b;
        }
        private static void Fit(GameObject root,float extent,bool height)
        {
            Bounds b=BoundsOf(root);
            float scale=extent/(height?b.size.y:Mathf.Max(b.size.x,b.size.z));
            root.transform.localScale*=scale;
            b=BoundsOf(root);
            root.transform.position+=new Vector3(-b.center.x,-b.min.y,-b.center.z);
        }
        private static void Photo(GameObject root,string name,Vector3 direction,float factor=1f)
        {
            Bounds b=BoundsOf(root);
            float size=Mathf.Max(b.size.x,Mathf.Max(b.size.y,b.size.z));
            Vector3 target=b.center;
            camera.transform.position=target+direction.normalized*size*factor*2.05f;
            camera.transform.LookAt(target);
            var rt=RenderTexture.GetTemporary(900,540,24,RenderTextureFormat.ARGB32);
            var old=RenderTexture.active;
            var tex=new Texture2D(900,540,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture=rt;
                root.SetActive(false);
                camera.Render();
                RenderTexture.active=rt;
                tex.ReadPixels(new Rect(0,0,900,540),0,0);tex.Apply();
                var background=tex.GetPixels32();
                root.SetActive(true);
                camera.Render();
                // The shader is synchronous and the first draw prefills
                // material bindings and streamed texture data after activation.
                camera.Render();
                RenderTexture.active=rt;
                tex.ReadPixels(new Rect(0,0,900,540),0,0);
                tex.Apply();
                var px=tex.GetPixels32();int magenta=0,foreground=0,crushedPaint=0;
                for(int i=0;i<px.Length;i++)
                {
                    var c=px[i];var bg=background[i];
                    if(c.r>220 && c.b>220 && c.g<50)magenta++;
                    if(c.r<=3 && c.g<=3 && c.b<=3)crushedPaint++;
                    if(Math.Abs(c.r-bg.r)+Math.Abs(c.g-bg.g)+Math.Abs(c.b-bg.b)>24)foreground++;
                }
                Require(foreground>px.Length/400,name+" visible native subject pixels="+foreground);
                Require(magenta<20,name+" no error-magenta pixels");
                File.WriteAllBytes(Path.Combine(dir,name+".png"),tex.EncodeToPNG());
                if (name == "gt_upgraded")
                    Require(crushedPaint < 60, name + " no crushed paint in daylight: " + crushedPaint);
            }
            finally
            {
                camera.targetTexture=null;RenderTexture.active=old;
                Object.DestroyImmediate(tex);RenderTexture.ReleaseTemporary(rt);
            }
        }
        private static void Setup()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.65f,.70f,.77f);
            RenderSettings.ambientEquatorColor=new Color(.39f,.43f,.46f);
            RenderSettings.ambientGroundColor=new Color(.23f,.24f,.23f);
            RenderSettings.ambientIntensity=1f;
            RenderSettings.fog=false;
            var sun=new GameObject("QA fixed daylight (not gameplay)").AddComponent<Light>();
            sun.type=LightType.Directional;sun.intensity=1.25f;
            sun.color=new Color(1f,.96f,.87f);sun.shadows=LightShadows.Soft;
            sun.transform.rotation=Quaternion.Euler(47f,-35f,0f);
            RenderSettings.sun=sun;
            var floor=GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale=Vector3.one*4f;
            floor.transform.position=Vector3.down*.02f;
            var m=new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor",new Color(.27f,.30f,.27f));
            m.SetFloat("_Smoothness",.16f);
            floor.GetComponent<Renderer>().sharedMaterial=m;
            camera=new GameObject("QA native camera").AddComponent<Camera>();
            camera.enabled=false;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.backgroundColor=new Color(.36f,.43f,.51f);
            camera.nearClipPlane=.025f;camera.farClipPlane=100f;
            camera.fieldOfView=35f;camera.aspect=900f/540f;camera.allowHDR=false;
        }
        private static void Loot()
        {
            Require(AarisWeaponCatalog.TryLoad(),"production weapon catalogue loaded");
            var go=new GameObject("QA actual floor weapon");
            var pickup=go.AddComponent<AarisWeaponPickup>();
            Require(pickup.Initialize(new AarisWeaponState(AarisWeaponCatalog.Get("AssaultRifle"),123u),5),"actual M4A1 pickup initialization");
            var renderers=go.GetComponentsInChildren<Renderer>(true);
            var originals=new List<Material[]>();
            var stock=new List<Material>();
            bool custom=false;
            foreach(var r in renderers)
            {
                var mats=r.sharedMaterials;originals.Add(mats);
                var copy=(Material[])mats.Clone();
                for(int i=0;i<copy.Length;i++)if(copy[i]!=null && copy[i].shader.name=="Aaris/Mobile Subject Lit")
                {
                    custom=true;var m=new Material(copy[i]);m.shader=Shader.Find("Universal Render Pipeline/Lit");
                    m.shaderKeywords=copy[i].shaderKeywords;
                    m.SetColor("_BaseColor",Color.white);
                    stock.Add(m);copy[i]=m;
                }
                r.sharedMaterials=copy;
            }
            Require(custom,"production weapon uses subject finish");
            // The satin pickup is a single cached copy of the authored gun
            // material. Both instances retain all existing texture inputs.
            Material ground = originals.SelectMany(slots => slots)
                .First(mat => mat != null && mat.name.EndsWith(" Ground", StringComparison.Ordinal));
            var inventory = typeof(AarisWeaponInventory);
            var authored = (Material)inventory.GetMethod("AuthoredPbrFinish", Priv)
                .Invoke(null, new object[] { pickup.Spec, ground });
            var groundFinish = (Func<Material, Material>)Delegate.CreateDelegate(
                typeof(Func<Material, Material>), inventory.GetMethod("GroundPickupFinish", Priv));
            Require(ground != authored && ReferenceEquals(ground, groundFinish(authored)),
                "native floor finish is reused across pickups and separate from the held finish");
            for (int i = 0; i < 16; i++) groundFinish(authored);
            long beforeGroundCache = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++) groundFinish(authored);
            long groundBytes = GC.GetAllocatedBytesForCurrentThread() - beforeGroundCache;
            Require(groundBytes == 0, "256 warmed floor material lookups allocate " + groundBytes + " managed bytes");
            Require(ground.GetTexture("_BaseMap") == authored.GetTexture("_BaseMap") &&
                ground.GetTexture("_BumpMap") == authored.GetTexture("_BumpMap") &&
                ground.GetTexture("_MetallicGlossMap") == authored.GetTexture("_MetallicGlossMap"),
                "floor and held gun use the exact same authored PBR maps");
            Require(ground.GetFloat("_Smoothness") < authored.GetFloat("_Smoothness") &&
                ground.GetColor("_BaseColor").r > authored.GetColor("_BaseColor").r,
                "ground finish raises midtones and restrains specular gloss");
            var line=go.GetComponentInChildren<LineRenderer>(true);
            Require(line != null, "production pickup has one range-gated marker");
            line.enabled = false;
            Photo(go,"loot_stock_closeup",new Vector3(-1,.9f,-1.7f),.65f);
            line.enabled = true;
            Photo(go,"loot_stock_control",new Vector3(-1,1.05f,-1.7f),.75f);
            for(int i=0;i<renderers.Length;i++)renderers[i].sharedMaterials=originals[i];
            line.enabled = false;
            Photo(go,"loot_upgraded_closeup",new Vector3(-1,.9f,-1.7f),.65f);
            if(line!=null)line.enabled=true;
            Photo(go,"loot_upgraded",new Vector3(-1,1.05f,-1.7f),.75f);
            Photo(go,"loot_distance",new Vector3(-1,.38f,-1.7f),3.8f);
            Object.DestroyImmediate(go);
            foreach(var m in stock)Object.DestroyImmediate(m);
            var sidearmRoot = new GameObject("QA compact sidearm");
            var sidearm = sidearmRoot.AddComponent<AarisWeaponPickup>();
            Require(sidearm.Initialize(new AarisWeaponState(AarisWeaponCatalog.Get("Pistol"), 41u), 7),
                "production compact sidearm initializes");
            bool coatedSlide = false;
            foreach (var renderer in sidearmRoot.GetComponentsInChildren<MeshRenderer>())
                foreach (var slot in renderer.sharedMaterials)
                    if (slot != null && slot.shader.name == "Aaris/Mobile Subject Lit") coatedSlide = true;
            Require(coatedSlide, "compact sidearm uses subject finish");
            Object.DestroyImmediate(sidearmRoot);
            var crateHost = new GameObject("QA ammo crate factory");
            crateHost.SetActive(false);
            var crates = crateHost.AddComponent<AarisDeathCrateWorld>();
            var crate = (GameObject)typeof(AarisDeathCrateWorld).GetMethod("CreateVisual", Priv).Invoke(crates, null);
            crate.transform.SetParent(null, false);
            crate.transform.Find("Vertical loot marker").gameObject.SetActive(false);
            Photo(crate, "ammo_crate", new Vector3(-1, .9f, -1.5f), .95f);
            Object.DestroyImmediate(crate);
            Object.DestroyImmediate(crateHost);
            var create=typeof(AarisOfflineTacticalHUD).GetMethod("CreateMedicalVisual",Priv);
            foreach(bool helmet in new[]{false,true})
            {
                var supply=(GameObject)create.Invoke(null,new object[]{helmet});
                Require(supply.GetComponentsInChildren<Collider>(true).Length==0,"supply has no physics collider");
                var f=supply.GetComponent<MeshFilter>();
                Require(f!=null && f.sharedMesh.subMeshCount==(helmet?2:3),"shared render-only supply mesh");
                var duplicate=(GameObject)create.Invoke(null,new object[]{helmet});
                Require(duplicate.GetComponent<MeshFilter>().sharedMesh==f.sharedMesh,"supply mesh is reused");
                Object.DestroyImmediate(duplicate);
                Photo(supply,helmet?"helmet_upgraded":"medkit_upgraded",new Vector3(-1,1.1f,-1.6f),.9f);
                Object.DestroyImmediate(supply);
            }
        }
        private static void Vehicle(string path,string label)
        {
            var src=Resources.Load<GameObject>(path);
            Require(src!=null,label+" actual model loaded");
            var go=Object.Instantiate(src);
            var b=BoundsOf(go);
            if(b.size.y>Mathf.Max(b.size.x,b.size.z)*1.3f)
                go.transform.rotation=Quaternion.Euler(-90f,0,0);
            b=BoundsOf(go);
            if(b.size.x>b.size.z*1.1f)go.transform.rotation=Quaternion.Euler(0,90,0)*go.transform.rotation;
            Fit(go,4.35f,false);
            var host=new GameObject("QA production material factory");host.SetActive(false);
            var world=host.AddComponent<AarisVehicleWorld>();
            var fn=typeof(AarisVehicleWorld).GetMethod("AuthoredUrpMaterial",Priv);
            Photo(go,label+"_authored_control",new Vector3(-1,.62f,-1.4f),.72f);
            int coated=0;
            foreach(var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats=r.sharedMaterials;
                for(int i=0;i<mats.Length;i++)
                {
                    if(mats[i]==null)continue;
                    var result=(Material)fn.Invoke(world,new object[]{mats[i]});
                    if(result!=null)mats[i]=result;
                    if(mats[i].shader.name=="Aaris/Mobile Subject Lit")coated++;
                }
                r.sharedMaterials=mats;
            }
            Require(coated>0,label+" production body/opaque glazing finish applied");
            Photo(go,label+"_upgraded",new Vector3(-1,.62f,-1.4f),.72f);
            Object.DestroyImmediate(go);Object.DestroyImmediate(host);
        }
        private static void PlayerMaterials()
        {
            var src=Resources.Load<GameObject>("AarisCharacters/Primary/TacticalSoldier/TacticalSoldier");
            Require(src!=null,"actual main tactical rig loaded");
            var host=new GameObject("QA local finish factory");host.SetActive(false);
            var visual=host.AddComponent<AarisCharacterVisual>();
            var type=typeof(AarisCharacterVisual);
            type.GetField("tacticalSoldierLoaded",Priv).SetValue(visual,true);
            var fn=type.GetMethod("BuildRealisticSoldierMaterial",Priv);
            var distinct=new HashSet<Material>();var smoothness=new HashSet<float>();
            foreach(var r in src.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            foreach(var before in r.sharedMaterials)
            {
                if(before==null || !distinct.Add(before))continue;
                var after=(Material)fn.Invoke(visual,new object[]{before});
                if(after==null || after.shader.name!="Aaris/Mobile Subject Lit")continue;
                Require(after.GetTexture("_BaseMap")==before.GetTexture("_BaseMap") &&
                    after.GetTexture("_BumpMap")==before.GetTexture("_BumpMap"),"player original maps retained");
                Require(ReferenceEquals(after,fn.Invoke(visual,new object[]{before})),"player finish cache reused");
                smoothness.Add(after.GetFloat("_Smoothness"));
            }
            Require(smoothness.Count>=3,"distinct tactical material roughness profiles");
            report.Add("INFO Player pose evidence comes from AarisPlayerSlopeVisualQA in original-scene Play Mode, not isolated BakeMesh.");
            Object.DestroyImmediate(host);
        }
        private static void Bike()
        {
            var src=Resources.Load<GameObject>("AarisVehicles/Authored/AarisRoadBike");
            Require(src!=null,"actual authored roadside motorcycle loaded");
            var host=new GameObject("QA production roadside bike factory");
            var bike=host.AddComponent<AarisRoadsideBikeScenery>();
            var type=typeof(AarisRoadsideBikeScenery);
            type.GetMethod("SetupMaterials",Priv).Invoke(bike,null);
            var go=Object.Instantiate(src);
            var rs=go.GetComponentsInChildren<Renderer>(true);
            var layouts=(Material[][][])type.GetMethod("GetBikePaintLayouts",Priv).Invoke(bike,new object[]{rs});
            int upgraded=0;
            for(int r=0;r<rs.Length;r++)
            {
                if(layouts[0][r]==null)continue;
                rs[r].sharedMaterials=layouts[0][r];
                foreach(var m in layouts[0][r])
                    if(m!=null && m.shader.name=="Aaris/Mobile Subject Lit")upgraded++;
            }
            Require(upgraded>0,"production motorcycle paint and alloy finishes applied");
            Fit(go,2.25f,false);
            Photo(go,"bike_upgraded",new Vector3(-1,.54f,-1.6f),.75f);
            Object.DestroyImmediate(go);Object.DestroyImmediate(host);
        }
        private static void Contacts()
        {
            // Native PhysX fixture, same production contact component. Setup
            // uses reflection only in Editor; the warm loop calls a delegate.
            var type=typeof(AarisWeaponPickup).Assembly.GetType("Aaris.MapReview.AarisSubjectContacts");
            var host=new GameObject("QA contact fixture");host.SetActive(false);
            var contacts=host.AddComponent(type);
            type.GetMethod("Awake",Priv).Invoke(contacts,null);
            var entries=(Array)type.GetField("entries",Priv).GetValue(contacts);
            var root=new GameObject("QA contact subject");
            root.transform.position=Vector3.up*.08f;
            var e=entries.GetValue(0);var et=e.GetType();
            et.GetField("root",Priv).SetValue(e,root.transform);
            et.GetField("size",Priv).SetValue(e,new Vector2(.8f,.65f));
            et.GetField("range",Priv).SetValue(e,22f);
            et.GetField("clearance",Priv).SetValue(e,.20f);
            entries.SetValue(e,0);
            camera.enabled=true;camera.transform.position=new Vector3(0,2,-3);
            camera.transform.LookAt(root.transform.position);
            type.GetField("cameraCache",Priv).SetValue(contacts,camera);
            Physics.SyncTransforms();
            var update=(Action)Delegate.CreateDelegate(typeof(Action),contacts,type.GetMethod("LateUpdate",Priv));
            update();
            var draw=type.GetProperty("LastDrawCount",Priv);
            var probes=type.GetProperty("LastProbeCount",Priv);
            Require((int)draw.GetValue(contacts)==1 && (int)probes.GetValue(contacts)<=2,
                "native floor contact accepted within bounded probe count");
            var leftFoot = new GameObject("QA left ankle").transform;
            var rightFoot = new GameObject("QA right ankle").transform;
            leftFoot.SetParent(root.transform, false);
            rightFoot.SetParent(root.transform, false);
            leftFoot.localPosition = new Vector3(-.16f, .06f, 0f);
            rightFoot.localPosition = new Vector3(.16f, .06f, 0f);
            e = entries.GetValue(0);
            et.GetField("leftFoot", Priv).SetValue(e, leftFoot);
            et.GetField("rightFoot", Priv).SetValue(e, rightFoot);
            entries.SetValue(e, 0);
            update();
            Require((int)draw.GetValue(contacts) == 2, "two planted ankles produce two contacts");
            leftFoot.localPosition += Vector3.up * .40f;
            update();
            Require((int)draw.GetValue(contacts) == 1, "lifted foot no longer draws a ground contact");
            leftFoot.localPosition -= Vector3.up * .40f;
            for(int i=0;i<64;i++)update();
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<256;i++)update();
            long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
            Require(bytes==0,"256 warmed native contact updates allocate "+bytes+" managed bytes");
            root.transform.position=Vector3.up*3;
            update();
            Require((int)draw.GetValue(contacts)==0,"airborne contact rejected");
            Object.DestroyImmediate(root);Object.DestroyImmediate(host);camera.enabled=false;
        }
        private static void Variants()
        {
            var shader=Shader.Find("Aaris/Mobile Subject Lit");
            Require(shader!=null && shader.isSupported,"subject shader supported");
            foreach(var message in ShaderUtil.GetShaderMessages(shader))
                Require(message.severity!=ShaderCompilerMessageSeverity.Error,"shader message: "+message.message);
            var pass=ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            Require(pass.Name=="SubjectForward","one native forward Lit pass");
            foreach(var platform in new[]{ShaderCompilerPlatform.OpenGLCore,ShaderCompilerPlatform.GLES3x,ShaderCompilerPlatform.Vulkan})
            foreach(var keywords in new[]{new string[0],new[]{"_NORMALMAP"},new[]{"_NORMALMAP","_METALLICSPECGLOSSMAP","INSTANCING_ON"},new[]{"_NORMALMAP","_EMISSION","FOG_LINEAR"},new[]{"_AARIS_AIRFRAME","_NORMALMAP","_EMISSION","FOG_LINEAR"}})
            {
                var stage=ShaderType.Vertex;
                var target=platform==ShaderCompilerPlatform.OpenGLCore?BuildTarget.StandaloneLinux64:BuildTarget.Android;
                var v=pass.CompileVariant(stage,keywords,platform,target);
                bool ok=v.Success && v.ShaderData!=null && v.ShaderData.Length>0;
                if(v.Messages!=null)foreach(var m in v.Messages)
                {
                    if(m.severity==ShaderCompilerMessageSeverity.Error){ok=false;report.Add("ERROR "+m.message);}
                }
                Require(ok,platform+" native program "+string.Join("+",keywords));
                if (keywords.Length == 4 && platform == ShaderCompilerPlatform.GLES3x)
                {
                    string glsl = System.Text.Encoding.UTF8.GetString(v.ShaderData);
                    Require(glsl.Contains("#ifdef FRAGMENT") && (glsl.Contains("fwidth") || glsl.Contains("dFdx")),
                        "GLES3 combined program contains airframe fragment edge filtering");
                }
                if(platform==ShaderCompilerPlatform.Vulkan)
                {
                    var fragment=pass.CompileVariant(ShaderType.Fragment,keywords,platform,target);
                    bool errors=false;
                    if(fragment.Messages!=null)foreach(var m in fragment.Messages)
                    {
                        if(m.severity==ShaderCompilerMessageSeverity.Error){errors=true;report.Add("ERROR "+m.message);}
                    }
                    Require(!errors,"Vulkan fragment has no compiler error "+string.Join("+",keywords));
                    report.Add("INFO separate Vulkan fragment bytecode "+(fragment.ShaderData==null?0:fragment.ShaderData.Length));
                }
            }
        }
        internal static void PrepareAirfieldPortrait(AarisThirdPersonPlayer player)
        {
            // Surveyed Southern Airfield runway, within the original map.
            var point = WorldFloor(new Vector3(160f, 26f, -464f));
            var capsule = player.GetComponent<CharacterController>();
            capsule.enabled = false;
            Vector3 daylight = RenderSettings.sun != null
                ? -RenderSettings.sun.transform.forward : new Vector3(.7f, 0f, .3f);
            var heading = Quaternion.LookRotation(Vector3.ProjectOnPlane(daylight, Vector3.up).normalized);
            player.transform.SetPositionAndRotation(point + Vector3.up * .14f, heading);
            capsule.enabled = true;
            player.ResumeFromAerialDrop();
            player.CancelAutoSprint();
            player.SetEditorLocomotionQAInput(Vector2.zero);
            var inventory = player.GetComponent<AarisWeaponInventory>();
            if (!inventory.TryPickup(new AarisWeaponState(AarisWeaponCatalog.Get("AssaultRifle"), 61u), out _))
                throw new InvalidOperationException("Review rifle equip failed");
            if (!inventory.HasGun) inventory.ToggleFists();
            Physics.SyncTransforms();
            capsule.Move(Vector3.down * .25f);
        }

        private static GameObject landedKit;
        private static AarisWeaponPickup landedPickup;

        internal static void PlaceLandedLoot(AarisThirdPersonPlayer player)
        {
            var transform = player.transform;
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, forward);
            var centre = transform.position + forward * 5f;
            landedKit = (GameObject)typeof(AarisOfflineTacticalHUD).GetMethod("CreateMedicalVisual", Priv)
                .Invoke(null, new object[] { false });
            landedKit.transform.position = WorldFloor(centre + side * .57f);

            var rifle = new GameObject("QA production floor pickup");
            rifle.transform.position = WorldFloor(centre - side * .43f);
            landedPickup = rifle.AddComponent<AarisWeaponPickup>();
            if (!landedPickup.Initialize(new AarisWeaponState(AarisWeaponCatalog.Get("AssaultRifle"), 207u), 17))
                throw new InvalidOperationException("Production terrain pickup could not initialize");
        }

        private static Camera CreateAirfieldObserver()
        {
            var observer = new GameObject("QA landed subject camera").AddComponent<Camera>();
            observer.enabled = false;
            observer.clearFlags = CameraClearFlags.Skybox;
            observer.fieldOfView = 47f;
            observer.nearClipPlane = .04f;
            // This QA observer only needs the surrounding airfield. Limit
            // distant terrain work on headless software OpenGL.
            observer.farClipPlane = 420f;
            observer.allowHDR = false;
            observer.allowMSAA = false;
            return observer;
        }

        internal static AarisVehicleMotor CaptureLandedSubjects(AarisThirdPersonPlayer player, string directory)
        {
            Directory.CreateDirectory(directory);
            var observer = CreateAirfieldObserver();
            var playerTransform = player.transform;
            var rig = player.GetComponentInChildren<Animator>();
            var hips = rig.GetBoneTransform(HumanBodyBones.Hips);
            var finishes = new HashSet<string>();
            foreach (var skin in rig.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                foreach (var material in skin.sharedMaterials)
                    if (material != null && material.shader.name == "Aaris/Mobile Subject Lit")
                    {
                        var albedo = material.GetTexture("_BaseMap");
                        string meshUv = skin.sharedMesh != null &&
                            skin.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord0) ? "UV0" : "no UV0";
                        finishes.Add(material.name + " | " + (albedo == null ? "default albedo" : albedo.name)
                            + " | " + meshUv);
                    }
            if (finishes.Count == 0)
                throw new InvalidOperationException("Live player's opaque materials missed the subject pass");
            File.WriteAllLines(Path.Combine(directory, "live-finishes.txt"), finishes);
            var forward = Vector3.ProjectOnPlane(playerTransform.forward, Vector3.up).normalized;
            var side = Vector3.Cross(Vector3.up, forward);
            var focus = hips.position + Vector3.up * .12f;
            WriteWorldPhoto(observer, focus + forward * 2.65f + side * 1.0f + Vector3.up * .55f,
                focus, directory, "soldier_front");
            WriteWorldPhoto(observer, focus - forward * 3.8f + side * .6f + Vector3.up * .8f,
                focus, directory, "soldier_back");
            if (Environment.GetEnvironmentVariable("AARIS_CONTACT_REVIEW") == "1")
            {
                var left = rig.GetBoneTransform(HumanBodyBones.LeftFoot);
                var right = rig.GetBoneTransform(HumanBodyBones.RightFoot);
                Vector3 ankles = (left.position + right.position) * .5f;
                Vector3 feetEye = ankles - forward * 1.9f + side * .72f + Vector3.up * .54f;
                WriteWorldPhoto(observer, feetEye, ankles, directory, "feet_control");
                WriteWorldPhoto(observer, feetEye, ankles, directory, "feet_contact",
                    camera => QueueReviewContacts(camera, playerTransform, 2, false,
                        directory, "standing"));
            }
            var marker = landedPickup.GetComponentInChildren<LineRenderer>();
            if (marker == null || !marker.enabled)
                throw new InvalidOperationException("Nearby gameplay pickup did not enable the production marker");
            Vector3 subject = (landedKit.transform.position + landedPickup.transform.position) * .5f
                + Vector3.up * .13f;
            Vector3 angle = (side * .9f - forward * .35f + Vector3.up * .50f).normalized;
            // This observer never drives
            // production visibility. The actual main camera enabled the marker.
            marker.enabled = false;
            landedPickup.transform.Find("Loot centered rotation pivot").localRotation = Quaternion.identity;
            WriteWorldPhoto(observer, subject + angle * 2.5f, subject, directory, "terrain_loot_2m5");
            Vector3 distantEye = subject + angle * 9f;
            WriteWorldPhoto(observer, distantEye, subject, directory, "terrain_loot_9m");
            CaptureHeldFinishControl(observer, distantEye, subject, directory);
            marker.enabled = true;
            WriteWorldPhoto(observer, subject + angle * 9f, subject, directory, "terrain_loot_9m_marker");
            var vehicles = Object.FindObjectsByType<AarisVehicleMotor>(FindObjectsSortMode.None);
            AarisVehicleMotor nearest = null;
            float nearestDistance = float.PositiveInfinity;
            foreach (var vehicle in vehicles)
            {
                float distance = (vehicle.transform.position - playerTransform.position).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = vehicle;
                    nearestDistance = distance;
                }
            }
            if (nearest != null)
            {
                var car = nearest.transform;
                Vector3 target = car.position + Vector3.up * .38f;
                Vector3 eye = target + car.forward * 5.6f + car.right * 4.8f + Vector3.up * 2.4f;
                WriteWorldPhoto(observer, eye, target, directory, "streamed_vehicle");
            }
            Object.Destroy(landedPickup.gameObject);
            Object.Destroy(landedKit);
            Object.Destroy(observer.gameObject);
            return nearest;
        }

        internal static void PlaceRoadContactFixture(AarisVehicleMotor motor, AarisThirdPersonPlayer player)
        {
            // Move the real streamed car onto the clear runway in Play Mode.
            // The existing WheelColliders settle before the driving sample.
            var body = motor.GetComponent<Rigidbody>();
            Vector3 start = WorldFloor(player.transform.position + Vector3.forward * 7f);
            body.position = start + Vector3.up * .67f;
            body.rotation = Quaternion.identity;
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            body.WakeUp();
            Physics.SyncTransforms();
        }

        internal static void CaptureRoadContacts(AarisVehicleMotor motor, string directory, bool grounded)
        {
            var observer = CreateAirfieldObserver();
            Transform car = motor.transform;
            Vector3 target = car.position - Vector3.up * .08f;
            Vector3 eye = target - car.forward * 5.6f + car.right * 4.2f + Vector3.up * 1.05f;
            if (grounded)
                WriteWorldPhoto(observer, eye, target, directory, "driving_control");
            string label = grounded ? "driving_contact" : "airborne_contact";
            WriteWorldPhoto(observer, eye, target, directory, label,
                camera => QueueReviewContacts(camera, car, grounded ? 1 : 0, true, directory, label));
            File.AppendAllText(Path.Combine(directory, "contact-review.txt"),
                label + " speed_mps=" + motor.TotalSpeedMetresPerSecond.ToString("F2",
                    System.Globalization.CultureInfo.InvariantCulture) + Environment.NewLine);
            Object.Destroy(observer.gameObject);
        }

        private static void QueueReviewContacts(Camera camera, Transform root, int expectedDraws,
            bool wheelPlane, string directory, string label)
        {
            // Invoke the production camera batch for the observer, with only
            // the pictured subject selected. Restore the live registrations.
            Type type = typeof(AarisWeaponPickup).Assembly.GetType("Aaris.MapReview.AarisSubjectContacts");
            var contacts = (Component)type.GetField("instance", Priv).GetValue(null);
            if (contacts == null) throw new InvalidOperationException("No live subject contact renderer");
            var entries = (Array)type.GetField("entries", Priv).GetValue(contacts);
            Type entryType = entries.GetType().GetElementType();
            var rootField = entryType.GetField("root", Priv);
            object selected = null;
            foreach (var entry in entries)
                if ((Transform)rootField.GetValue(entry) == root) { selected = entry; break; }
            if (selected == null) throw new InvalidOperationException("Subject contact was not registered");
            entryType.GetField("nextProbe", Priv).SetValue(selected, 0f);
            var saved = (Array)entries.Clone();
            try
            {
                Array.Clear(entries, 0, entries.Length);
                entries.SetValue(selected, 0);
                type.GetMethod("DrawContacts", Priv).Invoke(contacts, new object[] { camera });
                int draws = (int)type.GetProperty("LastDrawCount", Priv).GetValue(contacts);
                int probes = (int)type.GetProperty("LastProbeCount", Priv).GetValue(contacts);
                bool fromWheels = (bool)entryType.GetField("wheelPlane", Priv).GetValue(entries.GetValue(0));
                if (draws != expectedDraws || fromWheels != wheelPlane || probes > (wheelPlane ? 0 : 1))
                    throw new InvalidOperationException(label + " unexpected contact draws=" + draws +
                        " rays=" + probes + " wheels=" + fromWheels);
                File.AppendAllText(Path.Combine(directory, "contact-review.txt"),
                    label + " instances=" + draws + " rays=" + probes +
                    " wheels=" + fromWheels + Environment.NewLine);
            }
            finally { Array.Copy(saved, entries, entries.Length); }
        }

        // Photograph the same mesh and pose with the held gun's shared
        // settings, so marker visibility and framing cannot affect the comparison.
        private static void CaptureHeldFinishControl(Camera observer, Vector3 eye,
            Vector3 focus, string directory)
        {
            var renderers = landedPickup.GetComponentsInChildren<MeshRenderer>();
            Material[][] groundSlots = new Material[renderers.Length][];
            for (int i = 0; i < renderers.Length; i++)
                groundSlots[i] = renderers[i].sharedMaterials;
            try
            {
                typeof(AarisWeaponInventory).GetMethod("ApplyTextureAwareWeaponFinish", Priv)
                    .Invoke(null, new object[] { landedPickup.Spec, renderers, false });
                WriteWorldPhoto(observer, eye, focus, directory, "terrain_loot_9m_control");
            }
            finally
            {
                for (int i = 0; i < renderers.Length; i++)
                    renderers[i].sharedMaterials = groundSlots[i];
            }
        }

        private static Vector3 WorldFloor(Vector3 point)
        {
            if (Physics.Raycast(point + Vector3.up * 3f, Vector3.down, out var hit, 12f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return hit.point + Vector3.up * .012f;
            throw new InvalidOperationException("No actual map surface at loot review position");
        }

        private static void WriteWorldPhoto(Camera observer, Vector3 eye, Vector3 target,
            string directory, string label, Action<Camera> beforeRender = null)
        {
            observer.transform.position = eye;
            observer.transform.LookAt(target);
            var frame = RenderTexture.GetTemporary(960, 600, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(frame.width, frame.height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                observer.targetTexture = frame;
                observer.aspect = (float)frame.width / frame.height;
                beforeRender?.Invoke(observer);
                observer.Render();
                RenderTexture.active = frame;
                pixels.ReadPixels(new Rect(0, 0, frame.width, frame.height), 0, 0);
                pixels.Apply();
                File.WriteAllBytes(Path.Combine(directory, label + ".png"), pixels.EncodeToPNG());
            }
            finally
            {
                observer.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(frame);
                Object.Destroy(pixels);
            }
        }

        public static void Run()
        {
            dir=Environment.GetEnvironmentVariable("AARIS_ART_QA_DIR")??"/tmp/aaris-visual-qa";
            Directory.CreateDirectory(dir);
            report.Clear();
            bool deferredShaders = ShaderUtil.allowAsyncCompilation;
            // A cold async shader can render a placeholder during the very
            // frame readback. Native art evidence needs the compiled passes.
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                Require(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Null,"real native graphics device");
                Setup();Loot();Vehicle("AarisVehicles/Authored/1990s-suv","suv");
                Vehicle("AarisVehicles/PremiumRoadGT/AarisRoadGT","gt");
                Bike();PlayerMaterials();Contacts();Variants();
                report.Add("AARIS_SUBJECT_NATIVE_QA_PASS");
                File.WriteAllLines(Path.Combine(dir,"report.txt"),report);
                Debug.Log("AARIS_SUBJECT_NATIVE_QA_PASS");
                EditorApplication.Exit(0);
            }
            catch(Exception e)
            {
                report.Add(e.ToString());File.WriteAllLines(Path.Combine(dir,"report.txt"),report);
                Debug.LogException(e);EditorApplication.Exit(1);
            }
            finally
            {
                ShaderUtil.allowAsyncCompilation = deferredShaders;
            }
        }
    }
}
