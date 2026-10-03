"""GUO Device Test Manager: reproducible Android UI profiles and capture evidence."""
import os
import json
import hashlib
import re
import webbrowser
import queue
import subprocess
import threading
import time
from datetime import datetime
from pathlib import Path
import tkinter as tk
from tkinter import ttk, messagebox, filedialog

ROOT = Path(__file__).resolve().parents[2]
SDK = Path(os.environ.get('ANDROID_HOME', str(Path.home() / 'AppData/Local/Android/Sdk')))
ADB = SDK / 'platform-tools/adb.exe'
EMULATOR = SDK / 'emulator/emulator.exe'
OUT = ROOT / 'build/device-tests'
CATALOG = json.loads(Path(__file__).with_name('device_profiles.json').read_text(encoding='utf-8'))
PROFILES = {p['id']: p for p in CATALOG['profiles']}


def validate_catalog():
    names, ports = {}, {}
    for key, p in PROFILES.items():
        if not re.fullmatch(r'[a-z0-9_]+', p['avd']): raise ValueError('Invalid AVD name')
        if p['port'] % 2 or not 5554 <= p['port'] <= 5682: raise ValueError('Invalid emulator port')
        if p['avd'] in names and names[p['avd']] != p['port']: raise ValueError('AVD port mismatch')
        if p['port'] in ports and ports[p['port']] != p['avd']: raise ValueError('Duplicate emulator port')
        names[p['avd']] = p['port']; ports[p['port']] = p['avd']
        if p['size'] and (len(p['size']) != 2 or min(p['size']) < 320): raise ValueError('Invalid display size')
    if len(PROFILES) != len(CATALOG['profiles']): raise ValueError('Duplicate profile ID')
validate_catalog()
PACKAGE = 'org.guo.client'
HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0

def run(*args, timeout=45):
    result = subprocess.run([str(x) for x in args], capture_output=True, timeout=timeout, creationflags=HIDDEN)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors='replace') or result.stdout.decode(errors='replace'))
    return result.stdout.decode(errors='replace').strip()

def adb(serial, *args, timeout=45):
    return run(ADB, '-s', serial, *args, timeout=timeout)

class DeviceManager:
    def __init__(self, root):
        self.root = root
        self.messages = queue.Queue()
        self.busy = False
        self.recording = None
        self.active = None
        self.events = []
        self.selected = 'fold'
        OUT.mkdir(parents=True, exist_ok=True)
        self.settings_path=OUT/'settings.json'
        try: settings=json.loads(self.settings_path.read_text(encoding='utf-8'))
        except (OSError,ValueError): settings={}
        self.apk_path=settings.get('apk',str(ROOT/'build/android/GUO-debug.apk'))
        self.data_path=settings.get('data',os.environ.get('UO_CLIENT_DATA',''))
        root.title('GUO Device Test Manager')
        root.geometry('1060x820'); root.minsize(900, 800)
        pane = ttk.Frame(root, padding=18); pane.pack(fill='both', expand=True)
        ttk.Label(pane, text='GUO Device Test Manager', font=('Segoe UI', 23, 'bold')).pack(anchor='w')
        ttk.Label(pane, text='Android UI development • device profiles, posture transitions and reproducible captures').pack(anchor='w', pady=(2,12))
        self.search = tk.StringVar()
        searchrow=ttk.Frame(pane); searchrow.pack(fill='x')
        ttk.Label(searchrow,text='Find device').pack(side='left',padx=(0,8))
        ttk.Entry(searchrow,textvariable=self.search).pack(side='left',fill='x',expand=True)
        self.search.trace_add('write',lambda *_:self.populate())
        catalog=ttk.Frame(pane);catalog.pack(fill='x',pady=8)
        self.tree=ttk.Treeview(catalog,columns=('group','coverage'),show='tree headings',height=6,selectmode='browse')
        self.tree.heading('#0',text='Device / coverage family'); self.tree.column('#0',width=380)
        self.tree.heading('group',text='Family');self.tree.column('group',width=110)
        self.tree.heading('coverage',text='Emulation scope');self.tree.column('coverage',width=220)
        self.tree.pack(side='left',fill='x',expand=True); self.tree.bind('<<TreeviewSelect>>',self.describe)
        scrollbar=ttk.Scrollbar(catalog,orient='vertical',command=self.tree.yview)
        scrollbar.pack(side='right',fill='y');self.tree.configure(yscrollcommand=scrollbar.set)
        self.detail=tk.StringVar(); ttk.Label(pane,textvariable=self.detail,wraplength=970).pack(anchor='w')
        row=ttk.Frame(pane);row.pack(fill='x',pady=8)
        self.buttons=[]
        for label,fn in [('Launch / apply profile',self.prepare),('Install + run GUO',self.install_game),('Stop emulator',self.stop_device),('Save diagnostics',self.diagnostics)]:
            button=ttk.Button(row,text=label,command=lambda f=fn:self.task(f));button.pack(side='left',padx=(0,7));self.buttons.append(button)
        ttk.Button(row,text='Profile source',command=lambda:webbrowser.open(PROFILES[self.choice()]['source'])).pack(side='left')
        ttk.Button(row,text='APK / data folders',command=self.configure_paths).pack(side='left',padx=7)
        self.status=tk.StringVar(value='No active test device — select a profile and launch it.')
        ttk.Label(pane,textvariable=self.status,font=('Segoe UI',10,'bold')).pack(anchor='w',pady=3)
        self.section(pane,'Fold posture — native SDK foldable profiles only',[('Open flat',lambda:self.fold(2,180)),('Tabletop',lambda:self.fold(1,90,1)),('Book',lambda:self.fold(1,90,0)),('Closed cover',lambda:self.fold(0,0))])
        self.section(pane,'Rotation',[('Portrait',lambda:self.rotate(0)),('Landscape',lambda:self.rotate(1)),('Reverse portrait',lambda:self.rotate(2)),('Reverse landscape',lambda:self.rotate(3)),('Sensor / free',lambda:self.rotate(None))])
        self.section(pane,'Capture evidence',[('Start recording',self.start_record),('Stop + save',self.stop_record),('Screenshot',self.screenshot)])
        ttk.Button(pane,text='Open captures folder',command=lambda:os.startfile(OUT)).pack(anchor='w',pady=6)
        ttk.Label(pane,text='Clips: 3 minutes max, no audio. Captures include profile + transition metadata. Tent: choose it in GUO.\nSDK profiles use a Google APIs image; size presets do not emulate vendor firmware, GPU or built-in gamepads.',wraplength=980).pack(anchor='w')
        self.log = tk.Text(pane, height=12, wrap='word', font=('Consolas',10), state='disabled')
        self.log.pack(fill='both', expand=True, pady=(14,0))
        OUT.mkdir(parents=True, exist_ok=True)
        self.populate(); self.tree.selection_set('fold'); self.describe()
        self.messages.put(f'{len(PROFILES)} profiles available. Missing AVDs are created on launch from the installed Android 35 image.')
        root.after(100,self.drain)
        root.protocol('WM_DELETE_WINDOW',self.close)

    def section(self, pane, title, buttons):
        box=ttk.LabelFrame(pane,text=title,padding=10); box.pack(fill='x',pady=(12,0))
        for label, fn in buttons:
            button=ttk.Button(box,text=label,command=lambda f=fn:self.task(f)); button.pack(side='left',padx=3); self.buttons.append(button)

    def serial(self):
        return 'emulator-' + str(PROFILES[self.selected]['port'])

    def choice(self):
        selection=self.tree.selection()
        if not selection: raise ValueError('Select a device profile first.')
        return selection[0]

    def configure_paths(self):
        if self.busy or self.recording:
            messagebox.showinfo('Action in progress','Finish the current action or recording before changing paths.'); return
        dialog=tk.Toplevel(self.root);dialog.title('Device Test Manager · build and data');dialog.transient(self.root)
        apk=tk.StringVar(value=self.apk_path);data=tk.StringVar(value=self.data_path)
        for i,(label,value) in enumerate([('Debug APK',apk),('UO client data folder',data)]):
            ttk.Label(dialog,text=label).grid(row=i,column=0,padx=12,pady=12)
            ttk.Entry(dialog,textvariable=value,width=65).grid(row=i,column=1)
            def browse(v=value,kind=i):
                selected=filedialog.askopenfilename(parent=dialog,filetypes=[('Android APK','*.apk')]) if kind==0 else filedialog.askdirectory(parent=dialog)
                if selected: v.set(selected)
            ttk.Button(dialog,text='Browse',command=browse).grid(row=i,column=2,padx=12)
        def save():
            self.apk_path=apk.get();self.data_path=data.get()
            self.settings_path.write_text(json.dumps(dict(apk=self.apk_path,data=self.data_path),indent=2),encoding='utf-8')
            dialog.destroy()
        ttk.Button(dialog,text='Save paths',command=save).grid(row=2,column=1,pady=12)

    def populate(self):
        query=self.search.get().lower().strip()
        self.tree.delete(*self.tree.get_children())
        for key,p in PROFILES.items():
            if query in (' '.join([p['name'],p['group'],*p['covers']])).lower():
                self.tree.insert('', 'end', iid=key, text=p['name'], values=(p['group'],p['coverage']))
        visible=self.tree.get_children()
        if visible:self.tree.selection_set(visible[0])
        else:self.detail.set('No matching profiles. Clear the search or try a different device name.')

    def describe(self, *_):
        if not self.tree.selection(): return
        p=PROFILES[self.choice()]
        geometry='SDK display defaults' if not p['size'] else ' × '.join(map(str,p['size']))+f" px / {p['density']} test dpi"
        self.detail.set(geometry+' • '+p['coverage']+'\n'+(p['notes'] or 'Uses the Android SDK hardware definition with a Google APIs x86_64 system image.'))

    def assert_device(self, profile):
        serial=f"emulator-{profile['port']}"
        actual=adb(serial,'emu','avd','name').splitlines()[0]
        if actual!=profile['avd']: raise RuntimeError(f"{serial} belongs to {actual}, not {profile['avd']}.")

    def require_active(self):
        if self.selected!=self.active:
            raise RuntimeError('Launch / apply the selected profile first. Capture stays tied to the active profile.')
        self.assert_device(PROFILES[self.active])

    def ensure_avd(self, profile):
        if profile['avd'] in run(EMULATOR,'-list-avds').splitlines(): return
        image_dir=SDK / Path(CATALOG['system_image'].replace(';','/'))
        if not image_dir.is_dir():
            raise RuntimeError('Install '+CATALOG['system_image']+' with Android Studio SDK Manager first.')
        manager=SDK/'cmdline-tools/latest/bin/avdmanager.bat'
        if not manager.exists(): raise RuntimeError('Install Android SDK Command-line Tools (latest).')
        self.messages.put('Creating '+profile['avd']+' from SDK '+profile['sdk_device']+'…')
        result=subprocess.run([str(manager),'create','avd','-n',profile['avd'],'-k',CATALOG['system_image'],'-d',profile['sdk_device']],input='no\n',text=True,capture_output=True,timeout=120,creationflags=HIDDEN)
        if result.returncode: raise RuntimeError(result.stderr or result.stdout)
        self.event('avd-created',avd=profile['avd'])

    def event(self, action, **detail):
        self.events.append(dict(time=datetime.now().isoformat(),action=action,**detail))

    def metadata(self, name, profile_id, **extra):
        p=PROFILES[profile_id]; serial=f"emulator-{p['port']}"
        data=dict(profile=p,system_image=CATALOG['system_image'],captured=datetime.now().isoformat(),events=list(self.events),**extra)
        for key,args in [('display_size',('shell','wm','size')),('density',('shell','wm','density')),('rotation',('shell','wm','user-rotation'))]:
            try: data[key]=adb(serial,*args)
            except Exception as exc: data[key]='Unavailable: '+str(exc)
        try: data['git_revision']=run('git','-C',ROOT,'rev-parse','HEAD')
        except Exception: data['git_revision']='unknown'
        try: data['worktree_dirty']=bool(run('git','-C',ROOT,'status','--porcelain'))
        except Exception: data['worktree_dirty']=None
        data['apk_sha256']=getattr(self,'installed_apk_hash',None)
        (OUT/(name+'.json')).write_text(json.dumps(data,indent=2)+'\n',encoding='utf-8')

    def diagnostics(self):
        self.require_active()
        name=self.filename('.log')
        log=adb(self.serial(),'logcat','-d','-t','1500','-s','godot','AndroidRuntime')
        (OUT/name).write_text(log,encoding='utf-8')
        self.metadata(name,self.selected)
        self.messages.put('Saved diagnostics: '+str(OUT/name))

    def stop_device(self):
        self.require_active()
        if self.recording: raise RuntimeError('Stop and save the recording before stopping a device.')
        adb(self.serial(),'emu','kill')
        self.active=None
        self.messages.put('Emulator stopped; its data is preserved.')

    def task(self, fn):
        if self.busy:
            self.messages.put('Please wait for the current action to finish.'); return
        try:self.selected=self.record_profile if fn.__name__=='stop_record' and self.recording else self.choice()
        except ValueError as exc:self.messages.put(str(exc));return
        self.busy=True
        for button in self.buttons: button.configure(state='disabled')
        def work():
            try: fn()
            except Exception as exc: self.messages.put('ERROR: '+str(exc))
            finally: self.busy=False
        threading.Thread(target=work,daemon=True).start()

    def drain(self):
        while not self.messages.empty():
            self.log.configure(state='normal'); self.log.insert('end',self.messages.get()+'\n'); self.log.see('end'); self.log.configure(state='disabled')
        if not self.busy:
            for button in self.buttons: button.configure(state='normal')
        if self.recording: self.status.set('RECORDING • '+self.recording[3])
        elif self.active: self.status.set('Active: '+PROFILES[self.active]['name']+' • '+PROFILES[self.active]['coverage'])
        else: self.status.set('No active test device — select a profile and launch it.')
        self.root.after(100,self.drain)

    def prepare(self):
        if self.recording: raise RuntimeError('Stop and save the recording before switching devices.')
        profile=PROFILES[self.selected]; avd,port=profile['avd'],profile['port']; serial=self.serial()
        self.active=None
        self.installed_apk_hash=None
        self.ensure_avd(profile)
        devices=run(ADB,'devices')
        if serial not in devices:
            self.messages.put('Booting '+avd+'…')
            subprocess.Popen([str(EMULATOR),'-avd',avd,'-port',str(port),'-gpu','host','-feature','-Vulkan','-no-snapshot-load'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        end=time.monotonic()+150
        while time.monotonic()<end:
            try:
                if adb(serial,'shell','getprop','sys.boot_completed',timeout=5)=='1': break
            except Exception: pass
            time.sleep(2)
        else: raise RuntimeError('Emulator boot timed out.')
        actual=adb(serial,'emu','avd','name').splitlines()[0]
        if actual != avd: raise RuntimeError(f'{serial} is {actual}, not {avd}; close that emulator or change its port.')
        self.assert_device(profile)
        for line in run(ADB,'devices').splitlines()[1:]:
            fields=line.split()
            known={f"emulator-{p['port']}" for p in PROFILES.values()}
            if len(fields)==2 and fields[1]=='device' and fields[0] in known and fields[0]!=serial:
                other=next(p for p in PROFILES.values() if f"emulator-{p['port']}"==fields[0])
                if adb(fields[0],'emu','avd','name').splitlines()[0]==other['avd']:
                    adb(fields[0],'shell','am','force-stop',PACKAGE)
        if profile['size']:
            adb(serial,'shell','wm','size','x'.join(map(str,profile['size'])))
            adb(serial,'shell','wm','density',str(profile['density']))
        else:
            adb(serial,'shell','wm','size','reset'); adb(serial,'shell','wm','density','reset')
        adb(serial,'shell','wm','user-rotation','lock',str(profile['rotation']))
        self.active=self.selected
        self.events=[]; self.event('profile-applied',profile=self.selected)
        self.messages.put(profile['name']+' ready. Use Install + run GUO to deploy the current APK.')

    def install_game(self):
        self.require_active()
        if self.recording: raise RuntimeError('Stop recording before installing.')
        serial=self.serial()
        if not Path(self.apk_path).is_file(): raise RuntimeError('Choose an existing APK using APK / data folders.')
        self.messages.put('Installing current APK…')
        adb(serial,'install','-r',self.apk_path,timeout=120)
        with open(self.apk_path,'rb') as apk:self.installed_apk_hash=hashlib.file_digest(apk,'sha256').hexdigest()
        adb(serial,'reverse','tcp:2593','tcp:2593')
        remote='/sdcard/Android/data/'+PACKAGE+'/files/uo'
        try: adb(serial,'shell','test','-f',remote+'/tiledata.mul')
        except Exception:
            data=self.data_path
            if not data or not Path(data).is_dir(): raise RuntimeError('Set UO_CLIENT_DATA to the UO data folder, then prepare again.')
            self.messages.put('Copying UO data for first use; this can take several minutes…')
            adb(serial,'shell','mkdir','-p',remote)
            adb(serial,'push',str(Path(data))+'/.',remote,timeout=1200)
        adb(serial,'shell','am','force-stop',PACKAGE)
        adb(serial,'shell','monkey','-p',PACKAGE,'1')
        self.messages.put(self.selected+' ready. Use the emulator to play; capture controls are ready.')

    def fold(self,state,angle,rotation=None):
        self.require_active()
        if not PROFILES[self.selected]['fold']: raise RuntimeError('This profile has no native hinge. Select a Fold profile.')
        adb(self.serial(),'emu','sensor','set','hinge-angle0',str(angle))
        adb(self.serial(),'shell','cmd','device_state','state',str(state))
        if rotation is not None: self.rotate(rotation)
        self.event('fold',state=state,angle=angle)
        self.messages.put('Fold state '+str(state)+', hinge '+str(angle)+'°')

    def rotate(self,value):
        self.require_active()
        args=['free'] if value is None else ['lock',str(value)]
        adb(self.serial(),'shell','wm','user-rotation',*args)
        self.event('rotation',value=value)
        self.messages.put('Rotation: '+str(value if value is not None else 'sensor'))

    def filename(self,ext):
        return datetime.now().strftime('%Y%m%d-%H%M%S-%f')+'-'+self.selected+ext

    def start_record(self):
        self.require_active()
        if self.recording: raise RuntimeError('A recording is already active. Stop/save it first.')
        name=self.filename('.mp4'); remote='/data/local/tmp/'+name; serial=self.serial()
        pid=adb(serial,'shell',f'screenrecord --size 1280x1280 --bit-rate 8000000 --time-limit 180 {remote} >/dev/null 2>&1 & echo $!')
        if not pid.isdigit(): raise RuntimeError('Could not obtain recording process ID: '+pid)
        time.sleep(1)
        adb(serial,'shell','kill','-0',pid)
        self.recording=(serial,pid,remote,name)
        self.record_profile=self.selected
        self.record_start=datetime.now().isoformat()
        self.event('record-start',file=name)
        self.messages.put('RECORDING '+name+' — Stop + save when finished (maximum 3 minutes).')

    def stop_record(self):
        if not self.recording: raise RuntimeError('No active recording.')
        serial,pid,remote,name=self.recording
        try:
            command=adb(serial,'shell','cat',f'/proc/{pid}/cmdline')
            if 'screenrecord' in command and remote in command: adb(serial,'shell','kill','-2',pid)
        except RuntimeError: pass # The 180-second limit may already have stopped it.
        time.sleep(2)
        adb(serial,'pull',remote,OUT/name,timeout=120)
        self.event('record-stop',file=name)
        self.metadata(name,self.record_profile,started=self.record_start)
        self.recording=None
        self.messages.put('Saved '+str(OUT/name))

    def screenshot(self):
        self.require_active()
        name=self.filename('.png'); serial=self.serial(); remote='/data/local/tmp/'+name
        adb(serial,'shell','screencap','-p',remote)
        adb(serial,'pull',remote,OUT/name)
        self.event('screenshot',file=name)
        self.metadata(name,self.selected)
        self.messages.put('Saved '+str(OUT/name))

    def close(self):
        if self.recording:
            messagebox.showinfo('Recording active','Stop + save your recording before closing the manager.'); return
        if self.busy:
            messagebox.showinfo('Action in progress','Wait for the current action to finish.'); return
        self.root.destroy()

if __name__=='__main__':
    import argparse
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile',choices=list(PROFILES),help='Launch this profile on startup')
    args=parser.parse_args()
    root=tk.Tk(); manager=DeviceManager(root)
    if args.profile:
        manager.tree.selection_set(args.profile)
        root.after(300,lambda:manager.task(manager.prepare))
    root.mainloop()
