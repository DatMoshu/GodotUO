#if TOOLS
namespace GUO.Editor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using GUO.Workspace;

[Tool]
public partial class ServerManagerWindow : Window
{
    private ServerProfiles _profiles;
    private ClientRegistry _registry;
    private Func<ServerProfile, string> _state;
    private Func<string> _engine;
    private Action _changed;
    private ItemList _list, _clientList;
    private Label _status;
    private string _id, _clientId;
    private Process _build;
    private OptionButton _defaultClient, _kind;
    private Label _kindNote;
    private readonly Dictionary<string, LineEdit> _fields = new();
    private readonly Dictionary<string, LineEdit> _cfields = new();
    internal void Open(ServerProfiles profiles, ServerProfile selected, Func<ServerProfile,string> state, Func<string> engine, Action changed, ClientRegistry registry = null)
    {
        _profiles=profiles; _state=state; _changed=changed; _engine=engine; _registry=registry??ClientRegistry.Current;
        Title="GUO server manager"; Size=new Vector2I(960,720); CloseRequested+=QueueFree;
        var root=new VBoxContainer(); AddChild(root); root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var tabs=new TabContainer { SizeFlagsVertical=Control.SizeFlags.ExpandFill }; root.AddChild(tabs);
        BuildServers(tabs); BuildClients(tabs);
        _status=new Label { AutowrapMode=TextServer.AutowrapMode.WordSmart }; root.AddChild(_status);
        Refresh(); Fill(selected??NewServer());
        RefreshClients(); FillClient(_registry.Clients.FirstOrDefault()??new ClientProfile());
        if (DisplayServer.GetName() != "headless") PopupCentered();
    }
    private ServerProfile NewServer() => new ServerProfile { DefaultClient=_registry.Clients.FirstOrDefault()?.Id??"" };
    private void Guard(Action action) { try { action(); } catch(Exception e) { _status.Text=e.Message; } }
    private Button ActionButton(Container actions, string title, Action action) { var button=new Button { Text=title }; actions.AddChild(button); button.Pressed+=()=>Guard(action); return button; }
    private void AddField(VBoxContainer form, Dictionary<string,LineEdit> into, string key, string label, int mode, Func<int> browseMode = null)
    {
        form.AddChild(new Label { Text=label });
        var row=new HBoxContainer(); form.AddChild(row);
        var edit=new LineEdit { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; into[key]=edit; row.AddChild(edit);
        if(mode==0) return;
        var browse=new Button { Text="Browse" }; row.AddChild(browse);
        browse.Pressed+=()=> {
            int m=browseMode!=null ? browseMode() : mode;
            var dialog=new FileDialog { Access=FileDialog.AccessEnum.Filesystem, FileMode=m==2 ? FileDialog.FileModeEnum.OpenDir : FileDialog.FileModeEnum.OpenFile };
            AddChild(dialog); dialog.FileSelected+=path=> { edit.Text=path; dialog.QueueFree(); };
            dialog.DirSelected+=path=> { edit.Text=path; dialog.QueueFree(); }; dialog.Canceled+=dialog.QueueFree;
            dialog.PopupCentered(new Vector2I(700,450));
        };
    }
    private void BuildServers(TabContainer tabs)
    {
        var page=new VBoxContainer { Name="Servers" }; tabs.AddChild(page);
        page.AddChild(new Label { Text="Saved servers: configure each server's native listener and finish first-run setup before starting it here." });
        var split=new HSplitContainer { SizeFlagsVertical=Control.SizeFlags.ExpandFill }; page.AddChild(split);
        _list=new ItemList { CustomMinimumSize=new Vector2(210,0) }; split.AddChild(_list);
        _list.ItemSelected+=i=>Fill(_profiles.Servers[(int)i]);
        var scroll=new ScrollContainer { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; split.AddChild(scroll);
        var form=new VBoxContainer { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; scroll.AddChild(form);
        foreach (var (key,label,mode) in new[] {
            ("Backend","Server type",0),("Name","Display name",0),("Host","Host",0),("Port","Game port",0),
            ("Executable","Local server executable (blank for remote)",1),("ServerDirectory","Server working directory",2),
            ("ServerProject","Server code project (.csproj, optional)",1),("Arguments","Arguments (JSON array)",0) })
            AddField(form,_fields,key,label,mode);
        form.AddChild(new Label { Text="Default client" });
        _defaultClient=new OptionButton { TooltipText="The client the run bar selects with this server." }; form.AddChild(_defaultClient);
        AddField(form,_fields,"ExpectedClientVersion","Expected client version (optional, e.g. 7.0.107.76)",0);
        foreach (var (key,label,mode) in new[] { ("ContentLock","Client content lock (optional)",1),("ContentStore","Installed content store",2) })
            AddField(form,_fields,key,label,mode);
        var actions=new HFlowContainer(); page.AddChild(actions);
        ActionButton(actions,"New profile",()=>Fill(NewServer()));
        ActionButton(actions,"Save profile",SaveProfile);
        ActionButton(actions,"Add backend starters",AddStarters);
        ActionButton(actions,"Setup / validate",ValidateBackend);
        ActionButton(actions,"Remove from list",()=> {
            var s=_profiles.Servers.FirstOrDefault(s=>s.Id==_id);
            if(s!=null && ManagedServerProcess.Running(_state(s))) throw new InvalidOperationException("Stop the managed server first");
            if(s!=null) _profiles.Servers.Remove(s); _changed(); Refresh(); Fill(NewServer()); _status.Text="Profile removed; files kept.";
        });
        ActionButton(actions,"Open server files",()=>OpenFolder(_fields["ServerDirectory"].Text));
        ActionButton(actions,"Open server code",()=>OpenFolder(Path.GetDirectoryName(_fields["ServerProject"].Text)));
        ActionButton(actions,"Build server",()=>Build(_fields["ServerProject"].Text));
    }
    private void BuildClients(TabContainer tabs)
    {
        var page=new VBoxContainer { Name="Clients" }; tabs.AddChild(page);
        page.AddChild(new Label { Text="Clients: a program, the UO data it reads (in place, never copied or written) and what a shard layers over it." });
        var split=new HSplitContainer { SizeFlagsVertical=Control.SizeFlags.ExpandFill }; page.AddChild(split);
        _clientList=new ItemList { CustomMinimumSize=new Vector2(210,0) }; split.AddChild(_clientList);
        _clientList.ItemSelected+=i=>FillClient(_registry.Clients[(int)i]);
        var scroll=new ScrollContainer { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; split.AddChild(scroll);
        var form=new VBoxContainer { SizeFlagsHorizontal=Control.SizeFlags.ExpandFill }; scroll.AddChild(form);
        AddField(form,_cfields,"Name","Name",0);
        form.AddChild(new Label { Text="Kind" });
        _kind=new OptionButton(); foreach(string k in ClientKinds.All) _kind.AddItem(k); form.AddChild(_kind);
        _kindNote=new Label { AutowrapMode=TextServer.AutowrapMode.WordSmart }; form.AddChild(_kindNote);
        _kind.ItemSelected+=_=>KindNote();
        AddField(form,_cfields,"Program","Program (project folder, build or external executable)",1,()=>_kind.Selected==0 ? 2 : 1);
        AddField(form,_cfields,"Arguments","Arguments (JSON array; external expands {host} {port} {data} {slot})",0);
        AddField(form,_cfields,"WorkingDir","Working folder (blank: the program's folder)",2);
        AddField(form,_cfields,"BaseData","UO data folder (read only, blank: your configured one)",2);
        AddField(form,_cfields,"Overlay","Shard overlay folder (a guo_data.json folder; blank: this client's workspace folder)",2);
        AddField(form,_cfields,"Plugins","Plugins (JSON array of absolute paths; not for external)",0);
        AddField(form,_cfields,"Version","Client version (optional)",0);
        AddField(form,_cfields,"Encryption","Encryption (optional number)",0);
        var actions=new HFlowContainer(); page.AddChild(actions);
        ActionButton(actions,"New client",()=>FillClient(new ClientProfile()));
        ActionButton(actions,"Save client",SaveClient);
        ActionButton(actions,"Duplicate",()=> { var copy=_registry.Duplicate(_clientId); _registry.Save(); RefreshClients(); FillClient(copy); _status.Text="Duplicated."; });
        ActionButton(actions,"Remove client",()=> {
            if(_profiles.Servers.Any(s=>s.DefaultClient==_clientId)) throw new InvalidOperationException("A server uses this client by default; change that first");
            _registry.Remove(_clientId); _registry.Save(); RefreshClients(); FillClient(_registry.Clients.FirstOrDefault()??new ClientProfile()); _status.Text="Client removed; files kept.";
        });
        ActionButton(actions,"Record data fingerprint",()=> { var c=_registry.Find(_clientId)??throw new InvalidOperationException("Save the client first"); ClientRegistry.Refresh(c); _registry.Save(); _status.Text="Fingerprint recorded."; });
        ActionButton(actions,"Open client files",()=>OpenFolder(_cfields["BaseData"].Text));
        ActionButton(actions,"Open client code",()=>OpenFolder(_cfields["Program"].Text));
        ActionButton(actions,"Build client",()=>Build(Path.Combine(_cfields["Program"].Text,"GUO.csproj")));
        ActionButton(actions,"Open client editor",()=> {
            string project=string.IsNullOrEmpty(_cfields["Program"].Text) ? EditorWorkspace.HostProject : _cfields["Program"].Text;
            if(!File.Exists(Path.Combine(project,"project.godot"))) throw new InvalidDataException("Choose a Godot project folder");
            var info=new ProcessStartInfo(_engine()) { UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=project };
            foreach(string arg in new[] { "--editor","--path",project }) info.ArgumentList.Add(arg);
            Process.Start(info)?.Dispose();
        });
    }
    private void KindNote() => _kindNote.Text=_kind.Selected switch {
        0 => "A Godot project folder run by this editor's engine. Blank program: the project open now.",
        1 => "An exported GUO executable. Windows, Linux and macOS desktops only.",
        _ => "GUO only starts this program with these arguments and working folder. Nothing is injected. It runs with your rights. Desktop only." };
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
    private void RefreshClients()
    {
        _clientList.Clear(); foreach(var c in _registry.Clients) _clientList.AddItem(c.Name+" ("+c.Kind+")");
        _defaultClient.Clear(); _defaultClient.AddItem("None"); _defaultClient.SetItemMetadata(0,"");
        foreach(var c in _registry.Clients) { _defaultClient.AddItem(c.Name); _defaultClient.SetItemMetadata(_defaultClient.GetItemCount()-1,c.Id); }
    }
    private void SelectDefault(string id)
    {
        int at=0; for(int i=0;i<_defaultClient.GetItemCount();i++) if((string)_defaultClient.GetItemMetadata(i)==id) at=i;
        _defaultClient.Selected=at;
    }
    private void Fill(ServerProfile profile)
    {
        _id=profile.Id;
        foreach(var pair in _fields) { object value=typeof(ServerProfile).GetProperty(pair.Key).GetValue(profile); pair.Value.Text=pair.Key=="Arguments" ? JsonSerializer.Serialize(value) : value?.ToString()??""; }
        SelectDefault(profile.DefaultClient);
        int index=_profiles.Servers.FindIndex(s=>s.Id==_id); if(index>=0) _list.Select(index);
    }
    private void FillClient(ClientProfile c)
    {
        _clientId=c.Id;
        _cfields["Name"].Text=c.Name; _cfields["Program"].Text=c.Program; _cfields["WorkingDir"].Text=c.WorkingDir; _cfields["BaseData"].Text=c.BaseData; _cfields["Overlay"].Text=c.Overlay;
        _cfields["Arguments"].Text=JsonSerializer.Serialize(c.Arguments); _cfields["Plugins"].Text=JsonSerializer.Serialize(c.Plugins);
        _cfields["Version"].Text=c.Meta.Version??""; _cfields["Encryption"].Text=c.Meta.Encryption?.ToString()??"";
        _kind.Selected=Math.Max(0,Array.IndexOf(ClientKinds.All,c.Kind)); KindNote();
        int index=_registry.Clients.FindIndex(x=>x.Id==_clientId); if(index>=0) _clientList.Select(index);
    }
    private void SaveClient()
    {
        var previous=_registry.Find(_clientId);
        var c=new ClientProfile { Id=_clientId, Name=_cfields["Name"].Text.Trim(), Kind=ClientKinds.All[_kind.Selected], Program=_cfields["Program"].Text.Trim(),
            WorkingDir=_cfields["WorkingDir"].Text.Trim(), BaseData=_cfields["BaseData"].Text.Trim(), Overlay=_cfields["Overlay"].Text.Trim(),
            Arguments=JsonSerializer.Deserialize<string[]>(_cfields["Arguments"].Text)??Array.Empty<string>(), Plugins=JsonSerializer.Deserialize<string[]>(_cfields["Plugins"].Text)??Array.Empty<string>() };
        c.Meta=previous?.Meta??new ClientMeta();
        c.Meta.Version=_cfields["Version"].Text.Trim();
        string enc=_cfields["Encryption"].Text.Trim(); c.Meta.Encryption=enc.Length==0 ? null : int.Parse(enc);
        if(previous==null || previous.BaseData!=c.BaseData) ClientRegistry.Refresh(c);
        _registry.Add(c); _registry.Save(); RefreshClients(); SelectDefault(_profiles.Servers.FirstOrDefault(s=>s.Id==_id)?.DefaultClient); FillClient(c);
        _changed(); _status.Text="Client saved.";
    }
    private void SaveProfile()
    {
        var profile=new ServerProfile { Id=_id };
        foreach(var pair in _fields) typeof(ServerProfile).GetProperty(pair.Key).SetValue(profile,
            pair.Key=="Port" ? int.Parse(pair.Value.Text) : pair.Key=="Arguments" ? JsonSerializer.Deserialize<string[]>(pair.Value.Text) : pair.Value.Text.Trim());
        profile.DefaultClient=(string)_defaultClient.GetItemMetadata(_defaultClient.Selected)??"";
        var previous=_profiles.Servers.FirstOrDefault(s=>s.Id==_id);
        if(previous!=null && ManagedServerProcess.Running(_state(previous))) throw new InvalidOperationException("Stop this managed server before changing its profile");
        var updated=new ServerProfiles { Selected=profile.Id, SelectedClient=null, Servers=_profiles.Servers.Where(s=>s.Id!=_id).Append(profile).ToList() };
        ServerProfiles.Validate(updated); _profiles.Selected=updated.Selected; _profiles.SelectedClient=null; _profiles.Servers=updated.Servers;
        _changed(); Refresh(); Fill(profile); _status.Text="Profile saved.";
    }
    private static void OpenFolder(string path) { if(!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing folder"); OS.ShellOpen(path); }
    private static JsonDocument Backends() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(EditorData.RepoRoot,"tools","server_manager","backends.json")));
    private void AddStarters()
    {
        using var backends=Backends();
        string client=_registry.Clients.FirstOrDefault()?.Id??"";
        foreach(var b in backends.RootElement.EnumerateArray())
        {
            string id=b.GetProperty("id").GetString(); if(_profiles.Servers.Any(s=>s.Backend==id)) continue;
            var starter=new ServerProfile { Backend=id, Name=b.GetProperty("name").GetString()+" test server", Port=b.GetProperty("port").GetInt32(), DefaultClient=client };
            string home=Workspace.ServerHome(starter.Id);
            starter.ServerDirectory=home; starter.Executable=Path.Combine(home,b.GetProperty("executable").GetString());
            _profiles.Servers.Add(starter);
        }
        _changed(); Refresh(); _status.Text="Added starter profiles on ports 2610-2615. Select each and use Setup / validate before starting.";
    }
    private void ValidateBackend()
    {
        using var backends=Backends();
        var backend=backends.RootElement.EnumerateArray().FirstOrDefault(b=>b.GetProperty("id").GetString()==_fields["Backend"].Text);
        var client=_registry.Find((string)_defaultClient.GetItemMetadata(_defaultClient.Selected));
        string message="Executable: "+(File.Exists(_fields["Executable"].Text)?"present":"missing / remote")+"\nServer folder: "+(Directory.Exists(_fields["ServerDirectory"].Text)?"present":"missing")+"\nClient data: "+(client==null?"no client chosen":string.IsNullOrEmpty(client.BaseData)?"your configured data":Directory.Exists(client.BaseData)?"present":"missing")+"\n\n";
        string source=null;
        if(backend.ValueKind!=JsonValueKind.Undefined) { message+=backend.GetProperty("setup").GetString()+"\n\nGUO content adapter: "+backend.GetProperty("adapter").GetString()+"\n\n"; source=backend.GetProperty("source").GetString(); }
        message+="Configure the server listener for "+_fields["Host"].Text+":"+_fields["Port"].Text+". Give each instance separate save files and admin/bridge ports.\n\nValidation: start, login, enter world, save/restart, then prove content deployment. An open port alone does not prove compatibility.";
        var dialog=new AcceptDialog { Title="Setup and validation", DialogText=message }; AddChild(dialog);
        if(source!=null) { dialog.AddButton("Upstream setup",false,"upstream"); dialog.CustomAction+=action=> { if(action=="upstream") OS.ShellOpen(source); }; }
        dialog.Confirmed+=dialog.QueueFree; dialog.Canceled+=dialog.QueueFree; dialog.PopupCentered(new Vector2I(700,420));
    }
}
#endif
