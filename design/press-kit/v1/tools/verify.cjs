const fs=require('node:fs/promises'),path=require('node:path'),crypto=require('node:crypto');
const sharp=require(process.env.SHARP_MODULE||'sharp');
const root=path.resolve(__dirname,'..');
const assert=(ok,msg)=>{if(!ok)throw Error(msg)};
(async()=>{
 const manifest=JSON.parse(await fs.readFile(path.join(root,'manifest.json')));
 let rasters=0,containers=0;
 for(const item of manifest.files){
  const p=path.join(root,item.file),data=await fs.readFile(p);
  assert(data.length===item.bytes,`${item.file}: size`);
  assert(crypto.createHash('sha256').update(data).digest('hex')===item.sha256,`${item.file}: hash`);
  if(/\.(png|webp)$/.test(p)){
   const meta=await sharp(data).metadata();await sharp(data).raw().toBuffer();
   const dims=item.file.match(/-(\d+)x(\d+)/),square=item.file.match(/-(\d+)(?:-upscaled)?\.(png|webp)$/);
   if(dims)assert(meta.width===+dims[1]&&meta.height===+dims[2],`${item.file}: dimensions`);
   else if(square)assert(meta.width===+square[1]&&meta.height===+square[1],`${item.file}: square dimensions`);
   if(item.file.includes('native'))assert(meta.width===1254&&meta.height===1254,`${item.file}: native dimensions`);
   // Logos float on transparency: every export but the social cards, the
   // preview, the on-black masters and the apple-touch icon must carry alpha,
   // with at least 5% of its pixels fully clear.
   const onBlack=/^social\/|^preview\.png$|apple-touch-icon|masters\/[^/]+-native\.png$/.test(item.file)&&!item.file.includes('transparent');
   if(!onBlack){assert(meta.hasAlpha,`${item.file}: no alpha`);const {data:px}=await sharp(data).ensureAlpha().raw().toBuffer({resolveWithObject:true});let clear=0;for(let k=3;k<px.length;k+=4)if(px[k]===0)clear++;assert(clear*4/px.length>=0.05,`${item.file}: not transparent`);}
   rasters++;
  }
  if(p.endsWith('.ico')){
   assert(data.readUInt16LE(2)===1,'ICO signature');const count=data.readUInt16LE(4);
   for(let i=0;i<count;i++){const at=6+i*16,n=data[at]||256,length=data.readUInt32LE(at+8),offset=data.readUInt32LE(at+12);const m=await sharp(data.subarray(offset,offset+length)).metadata();assert(m.width===n&&m.height===n,'ICO entry');} containers++;
  }
  if(p.endsWith('.icns')){
   assert(data.toString('ascii',0,4)==='icns'&&data.readUInt32BE(4)===data.length,'ICNS header');let at=8,count=0;
   while(at<data.length){const length=data.readUInt32BE(at+4);assert(length>8&&at+length<=data.length,'ICNS chunk bounds');await sharp(data.subarray(at+8,at+length)).metadata();at+=length;count++;}assert(count===7,'ICNS entries');containers++;
  }
 }
 const html=await fs.readFile(path.join(root,'index.html'),'utf8');
 for(const match of html.matchAll(/(?:src|href)="([^"]+)"/g)){if(!match[1].startsWith('http'))await fs.access(path.join(root,match[1]));}
 console.log(`PASS: ${manifest.files.length} hashes; ${rasters} raster decodes; ${containers} icon containers; preview links.`);
})().catch(e=>{console.error(e);process.exit(1)});
