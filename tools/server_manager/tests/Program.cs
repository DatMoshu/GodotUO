using GUO.Editor;
using System.Diagnostics;
using System.Text.Json;
if(args.Length>0 && args[0]=="child") { Thread.Sleep(60000); return; }
string home=Path.Combine(Path.GetTempPath(),"guo-manager-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(home);
string a=Path.Combine(home,"a.json"), b=Path.Combine(home,"b.json");
var profile=new ServerProfile { Name="Test A", Executable=Environment.ProcessPath, ServerDirectory=home, Arguments=new[] { "child" } };
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
} finally { ManagedServerProcess.Stop(a); ManagedServerProcess.Stop(b); Directory.Delete(home,true); }
