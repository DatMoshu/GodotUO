#if TOOLS
namespace GUO.Editor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

[Tool]
public partial class ServerManagerWindow : Window
{
    private ServerProfiles _profiles;
    private Func<ServerProfile, string> _state;
    private Action _changed;
    private ItemList _list;
    private Label _status;
    private string _id;
    private Process _build;
    private readonly Dictionary<string, LineEdit> _fields = new();
    internal void Open(ServerProfiles profiles, ServerProfile selected, Func<ServerProfile,string> state, Func<string> engine, Action changed)
    {
        _profiles=profiles; _state=state; _changed=changed;
        Title="GUO server manager"; Size=new Vector2I(960,720); CloseRequested+=QueueFree;
        var root=new VBoxContainer(); AddChild(root); root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(new Label { Text="Saved servers: configure each server's native listener and finish first-run setup before starting it here." });
        var split=new HSplitContainer { SizeFlagsVertical=Control.SizeFlags.ExpandFill }; root.AddChild(split);
        _list=new ItemList { CustomMinimumSize=new Vector2(210,0) }; split.AddChild(_list);
        _list.ItemSelected+=i=>Fill(_profiles.Servers[(int)i]);
        var scroll=new ScrollContainer { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; split.AddChild(scroll);
        var form=new VBoxContainer { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; scroll.AddChild(form);
        foreach (var (key,label,mode) in new[] {
            ("Backend","Server type",0),("Name","Display name",0),("Host","Host",0),("Port","Game port",0),
            ("Executable","Local server executable (blank for remote)",1),("ServerDirectory","Server working directory",2),
            ("ServerProject","Server code project (.csproj, optional)",1),("Arguments","Arguments (JSON array)",0),
            ("ClientProject","Client Godot project folder / code",2),("ClientData","Client data folder (read only)",2),
            ("ContentLock","Client content lock (optional)",1),("ContentStore","Installed content store",2) })
        {
            form.AddChild(new Label { Text=label });
            var row=new HBoxContainer(); form.AddChild(row);
            var edit=new LineEdit { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; _fields[key]=edit; row.AddChild(edit);
            if(mode==0) continue;
            var browse=new Button { Text="Browse" }; row.AddChild(browse);
            browse.Pressed+=()=> {
                var dialog=new FileDialog { Access=FileDialog.AccessEnum.Filesystem, FileMode=mode==2 ? FileDialog.FileModeEnum.OpenDir : FileDialog.FileModeEnum.OpenFile };
                AddChild(dialog); dialog.FileSelected+=path=> { edit.Text=path; dialog.QueueFree(); };
                dialog.DirSelected+=path=> { edit.Text=path; dialog.QueueFree(); }; dialog.Canceled+=dialog.QueueFree;
                dialog.PopupCentered(new Vector2I(700,450));
            };
        }
        var actions=new HFlowContainer(); root.AddChild(actions);
        void ActionButton(string title, Action action) { var button=new Button { Text=title }; actions.AddChild(button); button.Pressed+=()=> { try { action(); } catch(Exception e) { _status.Text=e.Message; } }; }
        ActionButton("New profile",()=>Fill(new ServerProfile { ClientProject=ProjectSettings.GlobalizePath("res://"), ClientData=EditorData.Setting("UO_CLIENT_DATA","") }));
        ActionButton("Save profile",SaveProfile);
        ActionButton("Add backend starters",AddStarters);
        ActionButton("Setup / validate",ValidateBackend);
        ActionButton("Remove from list",()=> {
            var s=_profiles.Servers.FirstOrDefault(s=>s.Id==_id);
            if(s!=null && ManagedServerProcess.Running(_state(s))) throw new InvalidOperationException("Stop the managed server first");
            if(s!=null) _profiles.Servers.Remove(s); _changed(); Refresh(); Fill(new ServerProfile()); _status.Text="Profile removed; files kept.";
        });
        ActionButton("Open server files",()=>OpenFolder(_fields["ServerDirectory"].Text));
        ActionButton("Open client files",()=>OpenFolder(_fields["ClientData"].Text));
        ActionButton("Open client code",()=>OpenFolder(_fields["ClientProject"].Text));
        ActionButton("Open server code",()=>OpenFolder(Path.GetDirectoryName(_fields["ServerProject"].Text)));
        ActionButton("Build client",()=>Build(Path.Combine(_fields["ClientProject"].Text,"GUO.csproj")));
        ActionButton("Build server",()=>Build(_fields["ServerProject"].Text));
        ActionButton("Open client editor",()=> {
            string project=_fields["ClientProject"].Text;
            if(!File.Exists(Path.Combine(project,"project.godot"))) throw new InvalidDataException("Choose a Godot project folder");
            var info=new ProcessStartInfo(engine()) { UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=project };
            foreach(string arg in new[] { "--editor","--path",project }) info.ArgumentList.Add(arg);
            Process.Start(info)?.Dispose();
        });
        _status=new Label { AutowrapMode=TextServer.AutowrapMode.WordSmart }; root.AddChild(_status);
        Refresh(); Fill(selected??new ServerProfile());
        if (DisplayServer.GetName() != "headless") PopupCentered();
    }
    private void Build(string project)
    {
        if (_build != null) throw new InvalidOperationException("A build is already running in this panel");
        if (!File.Exists(project) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an existing C# project; use the native toolchain for other server types");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=Path.GetDirectoryName(project) };
        foreach (string arg in new[] { "build", project, "--nologo" }) info.ArgumentList.Add(arg);
        _build = Process.Start(info); _status.Text="Building; compiler output goes to the editor process console.";
    }
    public override void _Process(double delta)
    {
        if (_build == null || !_build.HasExited) return;
        _status.Text = _build.ExitCode == 0 ? "Build passed." : "Build failed; see the editor process console.";
        _build.Dispose(); _build=null;
    }
    public override void _ExitTree() { _build?.Dispose(); _build=null; }
    private void Refresh() { _list.Clear(); foreach(var s in _profiles.Servers) _list.AddItem(s.Name+" - "+s.Host+":"+s.Port); }
    private void Fill(ServerProfile profile)
    {
        _id=profile.Id;
        foreach(var pair in _fields) { object value=typeof(ServerProfile).GetProperty(pair.Key).GetValue(profile); pair.Value.Text=pair.Key=="Arguments" ? JsonSerializer.Serialize(value) : value?.ToString()??""; }
        int index=_profiles.Servers.FindIndex(s=>s.Id==_id); if(index>=0) _list.Select(index);
    }
    private void SaveProfile()
    {
        var profile=new ServerProfile { Id=_id };
        foreach(var pair in _fields) typeof(ServerProfile).GetProperty(pair.Key).SetValue(profile,
            pair.Key=="Port" ? int.Parse(pair.Value.Text) : pair.Key=="Arguments" ? JsonSerializer.Deserialize<string[]>(pair.Value.Text) : pair.Value.Text.Trim());
        var previous=_profiles.Servers.FirstOrDefault(s=>s.Id==_id);
        if(previous!=null && ManagedServerProcess.Running(_state(previous))) throw new InvalidOperationException("Stop this managed server before changing its profile");
        var updated=new ServerProfiles { Selected=profile.Id, Servers=_profiles.Servers.Where(s=>s.Id!=_id).Append(profile).ToList() };
        ServerProfiles.Validate(updated); _profiles.Selected=updated.Selected; _profiles.Servers=updated.Servers;
        _changed(); Refresh(); Fill(profile); _status.Text="Profile saved.";
    }
    private static void OpenFolder(string path) { if(!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing folder"); OS.ShellOpen(path); }
    private static JsonDocument Backends() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(EditorData.RepoRoot,"tools","server_manager","backends.json")));
    private void AddStarters()
    {
        using var backends=Backends();
        foreach(var b in backends.RootElement.EnumerateArray())
        {
            string id=b.GetProperty("id").GetString(); if(_profiles.Servers.Any(s=>s.Backend==id)) continue;
            string home=Path.Combine(EditorData.RepoRoot,"build","servers",id);
            _profiles.Servers.Add(new ServerProfile { Backend=id, Name=b.GetProperty("name").GetString()+" test server", Port=b.GetProperty("port").GetInt32(),
                ServerDirectory=home, Executable=Path.Combine(home,b.GetProperty("executable").GetString()), ClientProject=ProjectSettings.GlobalizePath("res://"), ClientData=EditorData.Setting("UO_CLIENT_DATA","") });
        }
        _changed(); Refresh(); _status.Text="Added starter profiles on ports 2610-2615. Select each and use Setup / validate before starting.";
    }
    private void ValidateBackend()
    {
        using var backends=Backends();
        var backend=backends.RootElement.EnumerateArray().FirstOrDefault(b=>b.GetProperty("id").GetString()==_fields["Backend"].Text);
        string message="Executable: "+(File.Exists(_fields["Executable"].Text)?"present":"missing / remote")+"\nServer folder: "+(Directory.Exists(_fields["ServerDirectory"].Text)?"present":"missing")+"\nClient data: "+(Directory.Exists(_fields["ClientData"].Text)?"present":"missing")+"\n\n";
        string source=null;
        if(backend.ValueKind!=JsonValueKind.Undefined) { message+=backend.GetProperty("setup").GetString()+"\n\nGUO content adapter: "+backend.GetProperty("adapter").GetString()+"\n\n"; source=backend.GetProperty("source").GetString(); }
        message+="Configure the server listener for "+_fields["Host"].Text+":"+_fields["Port"].Text+". Give each instance separate save files and admin/bridge ports.\n\nValidation: start, login, enter world, save/restart, then prove content deployment. An open port alone does not prove compatibility.";
        var dialog=new AcceptDialog { Title="Setup and validation", DialogText=message }; AddChild(dialog);
        if(source!=null) { dialog.AddButton("Upstream setup",false,"upstream"); dialog.CustomAction+=action=> { if(action=="upstream") OS.ShellOpen(source); }; }
        dialog.Confirmed+=dialog.QueueFree; dialog.Canceled+=dialog.QueueFree; dialog.PopupCentered(new Vector2I(700,420));
    }
}
#endif
