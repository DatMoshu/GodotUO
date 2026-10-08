"""PLT15 reviewed raw scanner. Raw findings survive all semantic annotations.

Semantic bindings remain pinned to the reviewed overnight package, not clearance.
Reports contain rule IDs/counts/paths, never matches, offsets or snippets.
"""
import hashlib,json,os,re,stat,struct
from pathlib import Path,PurePosixPath
import pefile

POLICY_SHA='9693cf81de9522fde432f9074de1d7ff9daaeaa1bfc951415f57a88e7b92b626'
GUARD_SHA='36080f4afa8504189d77983742523d4fa851655c28b344a9a84a45f62d64bca9'
SOURCE_SHA='ffb2b408886a6ae8ea0406a1f73fe80ed759ccd2d561f1032378de4eaf8a4d01'
HOST_SHA='a21787456ae3dbe381762561f16153c79736f469c54736a823d27fba824189f1'
DEPS_EVIDENCE_SHA='f895c5659c95569d175ec2392e33003111e6406f056f839f2bae19a307c94623'
VB_PATH='data_GUO_windows_x86_64/Microsoft.VisualBasic.dll'
VB_SHA='f1928da80470e1f7a098a80124adb0d34b103ada30b8e95cd0bd1f4bf18fda61'
RULES=[('home_path',re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b|<)[A-Za-z0-9._-]+',re.I)),
 ('private_ipv4',re.compile(r'\b(?:192\.168|10\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01]))\.\d{1,3}\.\d{1,3}\b')),
 ('credential_assignment',re.compile(r'(?im)^\s*(?:password|api[_-]?key|access[_-]?token|GUO_EDITOR_MCP_TOKEN)\s*[:=]\s*["\x27]?[^\s"\x27,}]+'))]
def digest(data):return hashlib.sha256(data).hexdigest()
def safe_path(path):
 p=PurePosixPath(path)
 return bool(path) and bool(p.parts) and all(part not in ('','.','..') for part in path.split('/')) and '\\' not in path and not p.is_absolute() and '..' not in p.parts and ':' not in path and '\x00' not in path and str(p)==path
def load_bindings(review_root):
 MAIN=Path(review_root).resolve()
 # Immutable review anchors; callers cannot supply expected hashes or policy data.
 policy_bytes=(MAIN/'build/coordination/plt13/classification-policy-proposal.json').read_bytes()
 guard_bytes=(MAIN/'build/coordination/plt13/classification_candidate.py').read_bytes()
 source_bytes=(MAIN/'build/coordination/plt05/capture-02/source-manifest.json').read_bytes()
 if (digest(policy_bytes),digest(guard_bytes),digest(source_bytes))!=(POLICY_SHA,GUARD_SHA,SOURCE_SHA):raise ValueError('review_binding_drift')
 manifest=json.loads(source_bytes);records={r['path']:r['sha256'] for r in manifest['files']}
 if records.get('tools/plugin_host/src/CuoInternal.cs')!=HOST_SHA:raise ValueError('host_source_binding')
 host=(MAIN/'build/coordination/plt12/jobs/director7474-qa09-plt12-local-v1/stage/tools/plugin_host/src/CuoInternal.cs').read_bytes()
 if digest(host)!=HOST_SHA:raise ValueError('host_source_drift')
 deps_bytes=(MAIN/'build/coordination/plt13/deps-literal-and-schema-review.json').read_bytes()
 anchors=(policy_bytes,guard_bytes,deps_bytes);decode_bindings(anchors)
 return anchors
def decode_bindings(anchors):
 if not isinstance(anchors,tuple) or len(anchors)!=3 or any(type(x) is not bytes for x in anchors):raise ValueError('immutable_review_bytes_required')
 if tuple(digest(x) for x in anchors)!=(POLICY_SHA,GUARD_SHA,DEPS_EVIDENCE_SHA):raise ValueError('immutable_review_digest')
 policy_bytes,guard_bytes,deps_bytes=anchors
 ns={};exec(compile(guard_bytes,'<exact QA19-reviewed Python guards>','exec'),ns)
 deps={r['path']:r['original_sha256'] for r in json.loads(deps_bytes)['files']}
 return json.loads(policy_bytes),ns,deps
def decoded_views(data):
 yield data.decode('utf-8',errors='ignore')
 for enc in ('utf-16-le','utf-16-be'):
  for off in (0,1):yield data[off:off+(len(data)-off)//2*2].decode(enc,errors='ignore')
def raw_findings(path,data,denied):
 # Never normalize/mask/replace bytes, including deps files.
 views=list(decoded_views(data));hits={}
 for rule,rx in RULES:
  count=sum(len(rx.findall(v)) for v in views)
  if count:hits[rule]=count
 for value in denied:
  count=sum(len(re.findall(re.escape(value),v,re.I)) for v in views)
  if count:hits['local_deny_value']=hits.get('local_deny_value',0)+count
 p=PurePosixPath(path)
 if not safe_path(path):hits['unsafe_path']=1
 if path.lower().startswith('addons/'):hits['addon_resource_requires_editor_runtime_review']=1
 if any(x.lower() in ('accounts','profiles','credentials','.git','sources') for x in p.parts):hits['private_directory']=1
 if p.suffix.lower() in ('.mul','.uop','.idx','.ver','.pem','.key','.pfx','.p12','.keystore'):hits['client_data_or_key']=1
 return hits
def unique_json(data):
 def pairs(rows):
  out={}
  for k,v in rows:
   if k in out:raise ValueError('duplicate_json_key')
   out[k]=v
  return out
 return json.loads(data.decode('utf-8-sig','strict'),object_pairs_hook=pairs,parse_constant=lambda _:(_ for _ in ()).throw(ValueError('non_json_number')))
def deps_annotations(data,guard):
 obj=unique_json(data)
 if not isinstance(obj,dict) or not {'runtimeTarget','targets','libraries'}<=obj.keys() or not obj.keys()<={'runtimeTarget','compilationOptions','targets','libraries','runtimes'}:raise ValueError('unknown_deps_schema')
 rt=obj['runtimeTarget'];targets=obj['targets'];libraries=obj['libraries']
 if not isinstance(rt,dict) or not isinstance(rt.get('name'),str) or not isinstance(targets,dict) or rt['name'] not in targets or not isinstance(libraries,dict):raise ValueError('deps_schema_owner')
 annotations=[]
 for target,groups in targets.items():
  if not isinstance(groups,dict):raise ValueError('deps_target_shape')
  for lib,group in groups.items():
   meta=libraries.get(lib)
   if not isinstance(meta,dict) or meta.get('type') not in ('package','project') or not isinstance(group,dict) or not group.keys()<={'dependencies','runtime','runtimeTargets','native','resources','compile'}:raise ValueError('deps_library_owner')
   for branch in ('runtime','runtimeTargets'):
    assets=group.get(branch,{})
    if not isinstance(assets,dict):raise ValueError('deps_asset_map')
    for asset,fields in assets.items():
     if not isinstance(fields,dict):raise ValueError('deps_asset_fields')
     allowed={'assemblyVersion','fileVersion'}|({'rid','assetType'} if branch=='runtimeTargets' else set())
     if not fields.keys()<=allowed:raise ValueError('unknown_deps_asset_field')
     if branch=='runtimeTargets' and (fields.get('assetType')!='runtime' or not isinstance(fields.get('rid'),str) or not fields['rid']):continue
     if PurePosixPath(asset).suffix.lower() not in ('.dll','.exe'):continue
     for key,value in fields.items():
      if guard['deps_field'](['targets',target,lib,branch,asset,key],value):annotations.append({'kind':'deps_numeric_version_leaf','field':key,'parsed_owner':'validated_deps_runtime_asset','finding_removed':False})
 return annotations
def typedefs(data):
 # Minimal CLI table reader, no target assembly loading or caller metadata records.
 pe=pefile.PE(data=data);directory=pe.OPTIONAL_HEADER.DATA_DIRECTORY[14]
 if not directory.VirtualAddress:raise ValueError('no_cli_metadata')
 co=pe.get_offset_from_rva(directory.VirtualAddress)
 def block(off,n):
  if not 0<=off<=len(data)-n:raise ValueError('truncated_pe_metadata')
  return data[off:off+n]
 rva,size=struct.unpack('<II',block(co+8,8));mo=pe.get_offset_from_rva(rva);root=block(mo,size)
 if root[:4]!=b'BSJB':raise ValueError('cli_metadata_magic')
 vlen=struct.unpack_from('<I',root,12)[0];pos=16+vlen
 if pos+4>len(root):raise ValueError('metadata_version_length')
 _,count=struct.unpack_from('<HH',root,pos);pos+=4;streams={}
 for _ in range(count):
  if pos+8>len(root):raise ValueError('metadata_stream_header')
  off,n=struct.unpack_from('<II',root,pos);pos+=8;end=root.find(b'\0',pos)
  if end<pos or end-pos>32 or off+n>len(root):raise ValueError('metadata_stream_bounds')
  name=root[pos:end].decode('ascii');pos+=(end-pos+1+3)//4*4
  if name in streams:raise ValueError('duplicate_metadata_stream')
  streams[name]=root[off:off+n]
 table=streams.get('#~');strings=streams.get('#Strings')
 if table is None or strings is None or len(table)<24:raise ValueError('metadata_tables_missing')
 heaps=table[6];valid=struct.unpack_from('<Q',table,8)[0];pos=24;counts={}
 for i in range(64):
  if valid>>i&1:
   if pos+4>len(table):raise ValueError('table_counts_truncated')
   counts[i]=struct.unpack_from('<I',table,pos)[0];pos+=4
 if counts.get(3) or counts.get(5):raise ValueError('pointer_tables_unsupported')
 sw=4 if heaps&1 else 2;gw=4 if heaps&2 else 2
 def coded(t,bits):return 4 if max([counts.get(i,0) for i in t])>=1<<(16-bits) else 2
 pos+=counts.get(0,0)*(2+sw+3*gw)+counts.get(1,0)*(coded((0,26,35,1),2)+2*sw)
 width=4+2*sw+coded((2,1,27),2)+(4 if counts.get(4,0)>=65536 else 2)+(4 if counts.get(6,0)>=65536 else 2)
 def string(index):
  end=strings.find(b'\0',index)
  if index>=len(strings) or end<index:raise ValueError('typedef_string_bounds')
  return strings[index:end].decode('utf-8','strict')
 rows=[]
 for _ in range(counts.get(2,0)):
  if pos+width>len(table):raise ValueError('typedef_row_truncated')
  name=int.from_bytes(table[pos+4:pos+4+sw],'little');ns=int.from_bytes(table[pos+4+sw:pos+4+2*sw],'little');rows.append(string(ns)+'.'+string(name));pos+=width
 return sorted(rows)
def pe_version_leaves(data):
 pe=pefile.PE(data=data);owners=[]
 def walk(entries,version=False):
  for entry in entries:
   owned=version or getattr(entry,'id',None)==16
   if hasattr(entry,'directory'):walk(entry.directory.entries,owned)
   elif owned and hasattr(entry,'data'):
    s=entry.data.struct;off=pe.get_offset_from_rva(s.OffsetToData);n=s.Size
    if not 0<=off<=len(data)-n:raise ValueError('version_resource_bounds')
    owners.append((off,n))
 if hasattr(pe,'DIRECTORY_ENTRY_RESOURCE'):walk(pe.DIRECTORY_ENTRY_RESOURCE.entries)
 leaves=[]
 for group in getattr(pe,'FileInfo',[]):
  for info in group:
   if getattr(info,'Key',None)!=b'StringFileInfo':continue
   for table in getattr(info,'StringTable',[]):
    for key,value in table.entries.items():
     if key!=b'Assembly Version':continue
     off=table.entries_offsets[key][1];n=table.entries_lengths[key][1]*2
     if not any(a<=off and off+n<=a+length for a,length in owners):raise ValueError('version_leaf_owner')
     text=value.decode('utf-8','strict') if isinstance(value,bytes) else value
     if not re.fullmatch(r'\d+\.\d+\.\d+\.\d+',text):continue
     matches=[m for enc in ('utf-8','utf-16-le','utf-16-be') for m in re.finditer(re.escape(text.encode(enc)),data)]
     leaves.append({'field':'Assembly Version','parsed_owner':'PE_RT_VERSION_StringFileInfo','numeric_version':True,'encoding_aliases':len(matches),'aliases_confined_to_leaf':all(off-1<=m.start() and m.end()<=off+n+1 for m in matches)})
 return leaves
def annotate(path,data,bindings,domain='disk'):
 annotations=[];errors=[];h=digest(data)
 try:
  policy,guard,deps=decode_bindings(bindings)
  if not safe_path(path):raise ValueError('semantic_path_refusal')
  if path.endswith('.deps.json'):
   if domain!='disk' or deps.get(path)!=h:raise ValueError('deps_artifact_binding_refusal')
   annotations.extend(deps_annotations(data,guard))
  if path in policy['files']:
   if domain!='disk':raise ValueError('component_domain_refusal')
   if h!=policy['files'][path]['sha256']:raise ValueError('component_hash_mismatch')
   types=typedefs(data);flagged=[t for t in types if t.startswith(('GUO.Editor','ClassicUO','Microsoft.Xna','FNA')) or any(x in t for x in ('EditorMcp','EditorCapabilities','SpriteMotionDock'))]
   tag=guard['compatibility']({'path':path,'sha256':h,'types':types,'excluded_types':flagged},policy)
   if tag=='unclassified':raise ValueError('component_inventory_binding_refusal')
   annotations.append({'kind':tag,'typedef_count':len(types),'namespace_findings':len(flagged),'finding_removed':False,'unknown_method_typeref_native_dynamic_semantics':True})
  if path==VB_PATH:
   if domain!='disk':raise ValueError('version_domain_refusal')
   if h!=VB_SHA:raise ValueError('version_artifact_hash_mismatch')
   for leaf in pe_version_leaves(data):
    if leaf['aliases_confined_to_leaf']:annotations.append({'kind':'exact_pe_numeric_version_leaf_candidate',**leaf,'finding_removed':False})
 except (ValueError,KeyError,IndexError,TypeError,struct.error,UnicodeError,pefile.PEFormatError):errors.append('semantic_parse_or_binding_refusal')
 return annotations,errors
def scan_buffer(path,data,denied,bindings,domain='disk'):
 if type(data) is not bytes:raise ValueError('immutable_bytes_required')
 findings=raw_findings(path,data,denied);annotations,errors=annotate(path,data,bindings,domain)
 if PurePosixPath(path).suffix.lower() in ('.dll','.exe'):
  try:
   pe=pefile.PE(data=data)
   if len(pe.OPTIONAL_HEADER.DATA_DIRECTORY)>14 and pe.OPTIONAL_HEADER.DATA_DIRECTORY[14].VirtualAddress:
    ts=typedefs(data);count=sum(t.startswith(('GUO.Editor','ClassicUO','Microsoft.Xna','FNA')) or any(x in t for x in ('EditorMcp','EditorCapabilities','SpriteMotionDock')) for t in ts)
    if count:findings['namespace_typedef_refusal']=count
  except (ValueError,KeyError,IndexError,struct.error,UnicodeError,pefile.PEFormatError):errors.append('pe_typedef_coverage_refusal')
 return {'path':path,'original_bytes':len(data),'original_sha256':digest(data),'full_original_buffer_scanned':True,'raw_findings':findings,'semantic_annotations':annotations,'semantic_failures':errors,'finding_removal':False}
def pck_payloads(data):
 # Same immutable PCK buffer supplies framing, payload and checksum checks.
 def take(off,n):
  if not 0<=off<=len(data)-n:raise ValueError('truncated_pck')
  return data[off:off+n]
 magic,version,major,minor,patch,flags=struct.unpack('<6I',take(0,24))
 if magic!=0x43504447 or version not in (2,3,4) or flags&~2:raise ValueError('unsupported_pck')
 offset=struct.unpack('<Q',take(24,8))[0];pos=struct.unpack('<Q',take(32,8))[0] if version>=3 else 96
 if pos<(40 if version>=3 else 96) or pos>len(data)-4:raise ValueError('pck_directory_bounds')
 count=struct.unpack('<I',take(pos,4))[0];pos+=4
 if count>1000000:raise ValueError('pck_count')
 rows=[];seen=set()
 for _ in range(count):
  n=struct.unpack('<I',take(pos,4))[0];pos+=4
  if not 0<n<=16384:raise ValueError('pck_path_length')
  name=take(pos,n).rstrip(b'\0').decode('utf-8','strict').removeprefix('res://');pos+=n
  off,size=struct.unpack('<2Q',take(pos,16));pos+=16;md5=take(pos,16);pos+=16;ef=struct.unpack('<I',take(pos,4))[0];pos+=4
  if not safe_path(name) or name in seen or ef:raise ValueError('pck_path_or_flags')
  seen.add(name);rows.append((name,off+offset,size,md5))
 for name,off,size,md5 in rows:
  payload=take(off,size)
  if hashlib.md5(payload).digest()!=md5:raise ValueError('pck_payload_digest')
  yield name,payload
def scan_package(package,deny_file,review_root):
 report={'status':'BLOCKED_PROPOSAL_NOT_ACCEPTANCE','distribution_cleared':False,'rows':[],'failures':[],'coverage_complete':False,'policy_sha256':POLICY_SHA,'guard_sha256':GUARD_SHA}
 try:bindings=load_bindings(review_root)
 except Exception:
  report['failures'].append({'stage':'review_bindings','rule':'immutable_binding_unavailable'});return report
 try:
  deny_bytes=deny_file.read_bytes();denied=[x.strip() for x in deny_bytes.decode('utf-8','strict').splitlines() if x.strip() and not x.strip().startswith('#')]
  if not denied:raise ValueError('empty_deny')
  report['deny_source_sha256']=digest(deny_bytes);report['deny_rule_count']=len(denied)
 except (OSError,ValueError,UnicodeError):
  report['failures'].append({'stage':'deny_source','rule':'deny_unavailable_or_empty'});return report
 try:
  if not package.is_dir() or package.is_symlink() or getattr(package.lstat(),'st_file_attributes',0)&0x400:raise ValueError('unsafe_package_root')
  files=[]
  def walk_error(_):raise OSError('inventory_read_failure')
  for root,dirs,names in os.walk(package,followlinks=False,onerror=walk_error):
   retained_dirs=[]
   for name in dirs:
    child=Path(root)/name;s=child.lstat();files.append(child)
    if not stat.S_ISLNK(s.st_mode) and not getattr(s,'st_file_attributes',0)&0x400:retained_dirs.append(name)
   dirs[:]=retained_dirs
   for name in names:files.append(Path(root)/name)
  files.sort()
 except (OSError,ValueError,AttributeError):
  report['failures'].append({'stage':'inventory','rule':'package_inventory_failure'});return report
 pck_count=0
 for file in files:
  name=file.relative_to(package).as_posix()
  try:
   s=file.lstat()
   if stat.S_ISLNK(s.st_mode) or getattr(s,'st_file_attributes',0)&0x400:raise ValueError('reparse')
   if not file.is_file():continue
   data=file.read_bytes();report['rows'].append(scan_buffer(name,data,denied,bindings))
   if name=='GUO.pck':
    pck_count+=1
    for entry,payload in pck_payloads(data):
     row=scan_buffer(entry,payload,denied,bindings,domain='pck');row['path']='pck/'+entry;report['rows'].append(row)
  except (OSError,ValueError,UnicodeError,struct.error):report['failures'].append({'stage':'item','path':name,'rule':'read_or_pck_validation_failure'})
 if pck_count!=1:report['failures'].append({'stage':'inventory','rule':'required_pck_missing'})
 report['coverage_complete']=not report['failures'] and all(r['full_original_buffer_scanned'] for r in report['rows'])
 report['raw_finding_items']=sum(bool(r['raw_findings']) for r in report['rows']);report['semantic_refusal_items']=sum(bool(r['semantic_failures']) for r in report['rows'])
 report['adapter_parse_coverage_complete']=report['coverage_complete'] and not report['semantic_refusal_items']
 report['semantic_clearance']=False
 report['limits']=['Literal immutable-buffer visitation with decoding heuristics is not complete privacy detection','TypeDefs only; full TypeRefs/method/native/dynamic behavior and proprietary/acquisition rights remain unknown','No acceptance status or finding removal from annotations','No immutable original-package artifact membership acceptance is inferred from directory visitation']
 return report


if __name__ == '__main__':
 import argparse
 parser=argparse.ArgumentParser(description='Unmasked Windows package audit; never grants distribution clearance')
 parser.add_argument('--package',type=Path,required=True)
 parser.add_argument('--deny-file',type=Path,required=True)
 parser.add_argument('--review-root',type=Path,required=True,help='Checkout containing immutable reviewed build/coordination evidence')
 parser.add_argument('--out',type=Path,required=True)
 args=parser.parse_args()
 # Preserve original packages, review anchors and existing reports.
 output=args.out.resolve()
 if output.is_relative_to(args.package.resolve()) or output.is_relative_to((args.review_root/'build/coordination/plt12').resolve()):
  parser.error('audit output must be separate from package and reviewed artifacts')
 report=scan_package(args.package,args.deny_file,args.review_root)
 output.parent.mkdir(parents=True,exist_ok=True)
 with output.open('x',encoding='utf-8') as stream:json.dump(report,stream,indent=2);stream.write('\n')
 print(json.dumps({key:report.get(key) for key in ('status','coverage_complete','raw_finding_items','semantic_refusal_items','distribution_cleared')}))
 raise SystemExit(0 if report.get('coverage_complete') else 1)
