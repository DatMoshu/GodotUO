# SPDX-License-Identifier: BSD-2-Clause
"""Compile an all-section adapter with pinned upstream sources, then run real API/persistence probes."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
sys.path.insert(0,str(Path(__file__).with_name('tests')))
from guo import load_config
from server_adapters.adapter import render
from fixtures import full_export


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--backend',choices=['servuo','runuo'],required=True)
    parser.add_argument('--out',type=Path,required=True)
    parser.add_argument('--compile-only',action='store_true',help='No UO installation needed; does not claim runtime validation')
    a=parser.parse_args();out=a.out.resolve()
    if out.exists():parser.error('--out must be a fresh directory')
    if os.name!='nt':parser.error('The real Framework compiler/runtime probe runs on Windows')
    compiler=Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    out.mkdir(parents=True);source=out/'source';logs=[]
    pin=json.loads(Path(__file__).with_name('upstreams.json').read_text(encoding='utf-8-sig'))[a.backend]
    report={'backend':a.backend,'upstream':pin,'compiled':False,'runtime':False}
    def run(args,cwd=None,timeout=180):
        r=subprocess.run([str(x) for x in args],cwd=cwd,capture_output=True,text=True,errors='replace',timeout=timeout)
        logs.append(r.stdout+r.stderr)
        if r.returncode:raise RuntimeError('Command failed: '+str(args[0])+'\n'+(r.stdout+r.stderr)[-2000:])
        return r.stdout+r.stderr
    try:
        run(['git','init','-q',source]);run(['git','-C',source,'remote','add','origin',pin['repo']]);run(['git','-C',source,'fetch','-q','--depth','1','origin',pin['commit']]);run(['git','-C',source,'checkout','-q','FETCH_HEAD'])
        code=render(full_export(),a.backend)['Scripts/Custom/GUO/GUOContent.cs'];target=source/'Scripts/Custom/GUO/GUOContent.cs';target.parent.mkdir(parents=True,exist_ok=True);target.write_text(code,encoding='utf-8')
        if a.backend=='servuo':run(['dotnet','build',source/'ServUO.sln','-c','Release','-p:Platform=x64','--nologo','-v','quiet'])
        else:
            for name,folder,options in [('core','Server',['/target:exe','/unsafe','/out:'+str(source/'RunUO.exe')]),('scripts','Scripts',['/target:library','/unsafe','/define:NEWPARENT','/r:'+str(source/'RunUO.exe'),'/out:'+str(source/'Scripts.dll'),'/r:System.Drawing.dll','/r:System.Web.dll','/r:System.Data.dll'])]:
                response=out/(name+'.rsp');response.write_text('\n'.join(['/nologo',*['"'+v+'"' for v in options],*['"'+str(p)+'"' for p in (source/folder).rglob('*.cs')]]),encoding='utf-8');run([compiler,'@'+str(response)])
        exe='ServUO.exe' if a.backend=='servuo' else 'RunUO.exe'
        probe=Path(__file__).with_name('tests')/'AdapterProbe.cs'
        run([compiler,'/nologo','/define:'+a.backend.upper(),'/r:'+str(source/exe),'/r:'+str(source/'Scripts.dll'),'/out:'+str(source/'AdapterProbe.exe'),probe.resolve()])
        report['compiled']=True
        if not a.compile_only:
            data=load_config().client_data
            if not data.is_dir() or str(data)=='.':raise RuntimeError('Set UO_CLIENT_DATA for the runtime probe')
            os.environ['UO_CLIENT_DATA']=str(data.resolve())
            output=run([source/'AdapterProbe.exe'],cwd=source,timeout=60)
            if 'PASS: all seven sections' not in output:raise RuntimeError('Probe did not report success')
            report['runtime']=True
        print(json.dumps(report,indent=2));return 0
    except (RuntimeError,OSError,subprocess.TimeoutExpired) as e:
        report['error']=str(e);print(e,file=sys.stderr);return 1
    finally:
        (out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
        (out/'probe.log').write_text('\n'.join(logs),encoding='utf-8')

if __name__=='__main__':raise SystemExit(main())
