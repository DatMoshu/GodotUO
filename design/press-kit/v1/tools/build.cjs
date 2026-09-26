// Rebuild distribution sizes from the approved raster masters. Requires sharp.
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const sharp = require(process.env.SHARP_MODULE || 'sharp');
const root = path.resolve(__dirname, '..');
const sizes = [16, 24, 32, 48, 64, 96, 128, 180, 192, 256, 512, 1024];
async function save(name, data) {
  const dest = path.join(root, name);
  await fs.mkdir(path.dirname(dest), {recursive:true});
  await fs.writeFile(dest, data);
}
async function ico(src, name, dims) {
  const images = await Promise.all(dims.map(n => sharp(src).resize(n,n).png().toBuffer()));
  const header = Buffer.alloc(6 + dims.length*16);
  header.writeUInt16LE(1,2); header.writeUInt16LE(dims.length,4);
  let offset = header.length;
  dims.forEach((n,i) => {
    const at=6+i*16;
    header[at]=n===256?0:n; header[at+1]=n===256?0:n;
    header.writeUInt16LE(1,at+4); header.writeUInt16LE(32,at+6);
    header.writeUInt32LE(images[i].length,at+8); header.writeUInt32LE(offset,at+12);
    offset+=images[i].length;
  });
  await save(name,Buffer.concat([header,...images]));
}
async function icns(src,name) {
  const chunks=[];
  for(const [type,n] of [['icp4',16],['icp5',32],['icp6',64],['ic07',128],['ic08',256],['ic09',512],['ic10',1024]]) {
    const png=await sharp(src).resize(n,n).png().toBuffer();
    const h=Buffer.alloc(8); h.write(type); h.writeUInt32BE(png.length+8,4); chunks.push(h,png);
  }
  const h=Buffer.alloc(8); h.write('icns');h.writeUInt32BE(8+chunks.reduce((n,b)=>n+b.length,0),4);
  await save(name,Buffer.concat([h,...chunks]));
}
const BLACK={r:0,g:0,b:0,alpha:1}, CLEAR={r:0,g:0,b:0,alpha:0};
async function canvas(src,w,h,logoSize,name,background=BLACK) {
  const logo=await sharp(src).resize(logoSize,logoSize).png().toBuffer();
  await save(name,await sharp({create:{width:w,height:h,channels:4,background}}).composite([{input:logo,gravity:'centre'}]).png().toBuffer());
}
(async()=>{
  for(const mark of ['guo-emblem','guo-godot-mark']) {
    // Everything a logo is placed with floats on transparency; only the
    // social cards, which platforms show as a finished rectangle, keep black.
    const src=path.join(root,'masters',`${mark}-transparent-native.png`);
    const onBlack=path.join(root,'masters',`${mark}-native.png`);
    for(const n of sizes) await save(`png/${mark}-${n}.png`,await sharp(src).resize(n,n).png().toBuffer());
    for(const n of [256,512,1024]) await save(`web/${mark}-${n}.webp`,await sharp(src).resize(n,n).webp({quality:92,alphaQuality:100}).toBuffer());
    for(const n of [2048,4096]) await save(`hd/${mark}-${n}-upscaled.png`,await sharp(src).resize(n,n,{kernel:'lanczos3'}).png().toBuffer());
    await ico(src,`icons/${mark}.ico`,[16,24,32,48,64,128,256]);
    await icns(src,`icons/${mark}.icns`);
    await canvas(onBlack,1200,630,570,`social/${mark}-open-graph-1200x630.png`);
    await canvas(src,1920,1080,960,`hd/${mark}-full-hd-1920x1080.png`,CLEAR);
    await canvas(src,3840,2160,1920,`hd/${mark}-4k-3840x2160-upscaled.png`,CLEAR);
  }
  const face=path.join(root,'masters/guo-godot-mark-transparent-native.png');
  await ico(face,'web/favicon.ico',[16,32,48]);
  for(const [file,n] of [['favicon-16.png',16],['favicon-32.png',32],['android-chrome-192.png',192],['android-chrome-512.png',512]])
    await save(`web/${file}`,await sharp(face).resize(n,n).png().toBuffer());
  // iOS fills transparency with black anyway; say so rather than leave it to chance.
  await save('web/apple-touch-icon.png',await sharp(path.join(root,'masters/guo-godot-mark-native.png')).resize(180,180).png().toBuffer());
  await save('web/site.webmanifest',JSON.stringify({name:'GodotUO',short_name:'GUO',icons:[{src:'android-chrome-192.png',sizes:'192x192',type:'image/png',purpose:'any'},{src:'android-chrome-512.png',sizes:'512x512',type:'image/png',purpose:'any'}],theme_color:'#000000',background_color:'#000000',display:'standalone'},null,2));
  const previews=await Promise.all(['guo-emblem','guo-godot-mark'].map(async(mark,i)=>({input:await sharp(path.join(root,'masters',`${mark}-native.png`)).resize(600,600).png().toBuffer(),left:i*640+20,top:20})));
  await save('preview.png',await sharp({create:{width:1280,height:640,channels:3,background:'#000000'}}).composite(previews).png().toBuffer());
  const files=[];
  async function scan(dir) {for(const e of await fs.readdir(dir,{withFileTypes:true})){const p=path.join(dir,e.name);if(e.isDirectory()) await scan(p);else if(e.name!=='manifest.json'){const b=await fs.readFile(p);files.push({file:path.relative(root,p).replaceAll('\\','/'),bytes:b.length,sha256:crypto.createHash('sha256').update(b).digest('hex')});}}}
  await scan(root);
  await save('manifest.json',JSON.stringify({version:1,project:'GodotUO',nativeResolution:[1254,1254],background:'Transparent, from masters/*-transparent-native.png; social cards, preview and apple-touch-icon on black',generation:'Built-in image_gen; resized and packaged with sharp',files},null,2));
  console.log(`Built ${files.length} files plus manifest.json`);
})().catch(e=>{console.error(e);process.exit(1);});
