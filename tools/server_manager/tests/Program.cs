using GUO.Editor;
using GUO.Workspace;
using System.Diagnostics;
using System.Text.Json;
if(args.Length>0 && args[0]=="child") { Thread.Sleep(60000); return; }
string home=Path.Combine(Path.GetTempPath(),"guo-manager-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
string a=Path.Combine(home,"a.json"), b=Path.Combine(home,"b.json");
Workspace.RootOverride=Path.Combine(home,"workspace"); ClientRegistry.Reset();
var profile=new ServerProfile { Name="Test A", Executable=Environment.ProcessPath, ServerDirectory=home, Arguments=new[] { "child" } };
void Require(bool ok, string what) { if(!ok) throw new Exception(what); }
string Refused(Action action) { try { action(); return null; } catch(Exception e) when (e is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or PlatformNotSupportedException) { return e.Message; } }
try {
    var list=new ServerProfiles { Selected=profile.Id, Servers=new() { profile } }; string file=Path.Combine(home,"profiles.json"); list.Save(file);
    if(ServerProfiles.Load(file).Selected!=profile.Id) throw new Exception("Selection was not preserved");
    ManagedServerProcess.Start(profile,a); ManagedServerProcess.Start(profile,b);
    if(!ManagedServerProcess.Running(a)||!ManagedServerProcess.Running(b)) throw new Exception("Two independent processes must run");
    bool refused=false; try { ManagedServerProcess.Start(profile,a); } catch(InvalidOperationException) { refused=true; }
    if(!refused) throw new Exception("Duplicate start accepted");
    ManagedServerProcess.Stop(a); if(!ManagedServerProcess.Running(b)) throw new Exception("Stopping A affected B");
    string saved=File.ReadAllText(b); var state=JsonSerializer.Deserialize<ManagedServerProcess>(saved); state.Started++;
    File.WriteAllText(b,JsonSerializer.Serialize(state)); ManagedServerProcess.Stop(b);
    using(var child=Process.GetProcessById(state.Pid)) if(child.HasExited) throw new Exception("Mismatched identity stopped process");
    File.WriteAllText(b,saved); ManagedServerProcess.Stop(b);
    Console.WriteLine("PASS: profile persistence, concurrent servers, duplicate start refusal, stop isolation, recycled identity protection");

    // ---- ADR-0032: client profiles and the workspace ----
    string project=Path.Combine(home,"project"), data=Path.Combine(home,"uo data"), other=Path.Combine(home,"other data");
    Directory.CreateDirectory(project); Directory.CreateDirectory(data); Directory.CreateDirectory(other);
    File.WriteAllBytes(Path.Combine(data,"tiledata.mul"),new byte[10]); File.WriteAllBytes(Path.Combine(other,"tiledata.mul"),new byte[11]);

    // Migration from the earlier format: identical pairs share one client, the old file is kept, nothing is written into the install.
    string legacy=Path.Combine(home,"old","profiles.json"); Directory.CreateDirectory(Path.GetDirectoryName(legacy));
    string idA=new string('a',32), idB=new string('b',32), idC=new string('c',32);
    string Old(string id,string name,string proj,string dat)=>JsonSerializer.Serialize(new Dictionary<string,object> { ["Id"]=id,["Backend"]="custom",["Name"]=name,["Host"]="127.0.0.1",["Port"]=2610,["Executable"]="",["ServerDirectory"]="",["ServerProject"]="",["ClientProject"]=proj,["ClientData"]=dat,["ContentLock"]="",["ContentStore"]="",["Arguments"]=Array.Empty<string>() });
    File.WriteAllText(legacy,"{\"Selected\":\""+idB+"\",\"Servers\":["+Old(idA,"One",project,data)+","+Old(idB,"Two",project,data)+","+Old(idC,"Three",project,other)+"]}");
    string installBefore=string.Join("|",Directory.GetFiles(data).Select(f=>Path.GetFileName(f)+new FileInfo(f).Length));
    var clients=ClientRegistry.Current;
    Require(ServerProfiles.Migrate(legacy,clients),"migration did not run");
    var servers=ServerProfiles.Load(Workspace.ServersFile);
    Require(servers.Servers.Count==3 && clients.Clients.Count==2,"migration: expected 3 servers and 2 clients, got "+servers.Servers.Count+" and "+clients.Clients.Count);
    Require(servers.Servers[0].DefaultClient==servers.Servers[1].DefaultClient && servers.Servers[0].DefaultClient!=servers.Servers[2].DefaultClient,"migration did not dedupe identical pairs");
    Require(servers.Selected==idB && !File.Exists(legacy) && File.Exists(legacy+".migrated"),"migration: selection or backup");
    Require(!File.ReadAllText(Workspace.ServersFile).Contains("ClientProject"),"migrated file still names ClientProject");
    ClientRegistry.Reset(); var reloaded=ClientRegistry.Load();
    Require(reloaded.Clients.Count==2 && reloaded.Find(servers.Servers[0].DefaultClient).Program==project && reloaded.Find(servers.Servers[0].DefaultClient).BaseData==data && reloaded.Find(servers.Servers[0].DefaultClient).Meta.Source=="migrated","reload after migration");
    Require(string.Join("|",Directory.GetFiles(data).Select(f=>Path.GetFileName(f)+new FileInfo(f).Length))==installBefore && Directory.GetFiles(data).Length==1,"the install was touched");
    Require(!ServerProfiles.Migrate(legacy,reloaded),"a second migration ran");
    Console.WriteLine("PASS: migration of the earlier profiles (dedupe, backup, install untouched)");

    // Strict validation: unknown fields, relative paths, bad kinds and ids are refused; a refused file is never overwritten.
    Require(Refused(()=>JsonSerializer.Deserialize<ServerProfiles>("{\"Servers\":[{\"ClientProject\":\"x\"}]}",ServerProfiles.Json))!=null,"a legacy field was accepted in servers.json");
    Require(Refused(()=>ServerProfiles.Validate(new ServerProfiles { Servers=new() { new ServerProfile { DefaultClient="not-an-id" } } }))!=null,"bad default client id accepted");
    Require(Refused(()=>ServerProfiles.Validate(new ServerProfiles { Servers=new() { new ServerProfile { ExpectedClientVersion="seven" } } }))!=null,"bad expected version accepted");
    Require(Refused(()=>ClientRegistry.Validate(new[] { new ClientProfile { BaseData="relative/path" } }))!=null,"relative client path accepted");
    Require(Refused(()=>ClientRegistry.Validate(new[] { new ClientProfile { Kind="java" } }))!=null,"unknown kind accepted");
    Require(Refused(()=>ClientRegistry.Validate(new[] { new ClientProfile { Kind=ClientKinds.External } }))!=null,"external without program accepted");
    string good=File.ReadAllText(Workspace.ClientsFile);
    File.WriteAllText(Workspace.ClientsFile,good.Replace("\"version\": 1","\"version\": 1, \"surprise\": true"));
    ClientRegistry.Reset(); var broken=ClientRegistry.Current;
    Require(broken.LoadError!=null && Refused(()=>broken.Save())!=null && File.ReadAllText(Workspace.ClientsFile).Contains("surprise"),"an unknown field in clients.json was accepted or overwritten");
    File.WriteAllText(Workspace.ClientsFile,good); ClientRegistry.Reset();
    Console.WriteLine("PASS: strict validation (unknown fields, relative paths, kinds, ids) and no overwrite of a refused file");

    // Slots are keyed by server and client.
    var reg=ClientRegistry.Current; var c1=reg.Clients[0]; var c2=reg.Clients[1];
    string s1=Workspace.RunSlot(idA,c1.Id,1), s2=Workspace.RunSlot(idA,c2.Id,1), s3=Workspace.RunSlot(idB,c1.Id,1);
    Require(s1.EndsWith(Path.Combine("runs",idA,c1.Id,"slot-1")) && new[] { s1,s2,s3 }.Distinct().Count()==3,"slots are not keyed by server and client");
    Require(Refused(()=>Workspace.RunSlot(idA,c1.Id,5))!=null && Refused(()=>Workspace.RunSlot("..",c1.Id,1))!=null,"slot range or id traversal accepted");
    Console.WriteLine("PASS: run slots keyed by server and client");

    // Fingerprint and mismatch warning.
    c1.Meta.Version="7.0.107.76"; ClientRegistry.Refresh(c1);
    Require(c1.Meta.BaseFingerprint.Length==64,"no fingerprint");
    Require(ClientRegistry.Mismatch("7.0.107.76",c1)==null && ClientRegistry.Mismatch("",c1)==null,"false mismatch");
    Require(ClientRegistry.Mismatch("7.0.50.0",c1)?.Contains("7.0.50.0")==true,"version mismatch not reported");
    File.WriteAllBytes(Path.Combine(data,"tiledata.mul"),new byte[12]);
    Require(ClientRegistry.Mismatch("",c1)?.Contains("changed")==true,"data drift not reported");
    Console.WriteLine("PASS: version and data fingerprint warnings");

    // The external kind: a dry run, then an exact-process start. GUO adds no environment, no console redirect, no plugins.
    var external=new ClientProfile { Name="Fork", Kind=ClientKinds.External, Program=Environment.ProcessPath, Arguments=new[] { "child", "{host}:{port}", "{data}" }, BaseData=data, Plugins=new[] { Path.Combine(home,"p.dll") } };
    reg.Add(external);
    var plan=ClientLaunch.PlanExternal(external,"127.0.0.1",2610,idA,2);
    Require(plan.Arguments.SequenceEqual(new[] { "child","127.0.0.1:2610",data }) && plan.Environment.Count==0 && plan.Console==null && plan.SlotDir==Workspace.RunSlot(idA,external.Id,2),"external plan");
    Require(plan.WorkingDir==Path.GetDirectoryName(Environment.ProcessPath),"external working folder");
    Require(!reg.Files(external.Id,out _,out _),"an external client offered files to the running GUO");
    ClientLaunch.WritePlugins(plan.SlotDir,external); Require(!File.Exists(Path.Combine(plan.SlotDir,"settings.json")),"plugins were injected into an external client");
    Require(Refused(()=>ClientLaunch.PlanBuild(external,"127.0.0.1",2610,idA,1,home,""))!=null,"a build plan was made for an external client");
    ManagedServerProcess.Start(plan.ToStartInfo(),plan.State);
    Require(ManagedServerProcess.Running(plan.State),"external client not tracked");
    Require(Refused(()=>ManagedServerProcess.Start(plan.ToStartInfo(),plan.State))!=null,"a running external slot was started twice");
    ManagedServerProcess.Stop(plan.State); Require(!ManagedServerProcess.Running(plan.State),"external client not stopped");
    var build=new ClientProfile { Name="Build", Kind=ClientKinds.GuoBuild, Program=Environment.ProcessPath, BaseData=data, Plugins=new[] { Path.Combine(home,"p.dll") } };
    var bp=ClientLaunch.PlanBuild(build,"127.0.0.1",2610,idA,1,Path.Combine(home,"store"),"");
    Require(bp.Environment["UO_CACHE_DIR"]==Path.Combine(Workspace.RunSlot(idA,build.Id,1),"cache") && bp.Environment["UO_CLIENT_DATA"]==data && bp.Environment["UO_SHARD_PORT"]=="2610","build environment");
    ClientLaunch.WritePlugins(bp.SlotDir,build); Require(File.ReadAllText(Path.Combine(bp.SlotDir,"settings.json")).Contains("p.dll"),"plugins missing for a build");
    Console.WriteLine("PASS: external and build launch plans, exact-process tracking, no injection into external clients");

    // The pregame's lookup: a folder profile is found again, an overlay is the custom slot.
    string overlay=Path.Combine(home,"overlay"); Directory.CreateDirectory(overlay);
    var made=reg.FindOrAddFolder("Shard",data,overlay,"7.0.1.0",null,"pregame"); var again=reg.FindOrAddFolder("Other name",data,overlay,"",null,"pregame");
    Require(made.Id==again.Id && reg.Files(made.Id,out string custom,out string install) && custom==overlay && install==data,"folder profile lookup");
    reg.Save(); ClientRegistry.Reset(); Require(ClientRegistry.Current.Find(made.Id).Meta.Source=="pregame","client.json not persisted");
    Console.WriteLine("PASS: pregame folder lookup over the shared registry");
} finally { ManagedServerProcess.Stop(a); ManagedServerProcess.Stop(b); Directory.Delete(home,true); }
