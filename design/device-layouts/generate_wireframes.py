"""Render shareable wireframes from the actual C# layout resolver's exported geometry."""
import json
from pathlib import Path
from html import escape
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent
FONT = 'C:/Windows/Fonts/segoeui.ttf'
BOLD = 'C:/Windows/Fonts/segoeuib.ttf'
COLORS = {'World': '#e4e9dc', 'Companion': '#eae0ca', 'Movement': '#ccdbe1', 'Actions': '#ccdbe1', 'Hinge': '#302e2a'}
LABELS = {'World': 'WORLD', 'Companion': 'COMPANION', 'Movement': 'MOVE', 'Actions': 'A B X Y', 'Hinge': ''}
NOTES = {
 'phone': 'Compact world above; companion between virtual controls. Rotate without relogging.',
 'cover': 'Only the active outer display. Same character, compact companion and touch controls.',
 'flat': 'Open, continuous display. Top world and lower companion; no physical second display.',
 'tabletop': 'Horizontal fold: use reported hinge bounds and keep both sides of the hinge clear.',
 'book': 'Vertical fold: world left, companion right. Controls remain on the outer lower edges.',
 'tent': 'Manual Tent on supported hardware. With a controller, use the facing display for the world.',
 'tablet-landscape': 'Large window in dp. Wide world and lower companion between touch controls.',
 'tablet-portrait': 'Tall world above. Companion and controls below; preserve state on rotation.'
}

def font(size, bold=False): return ImageFont.truetype(BOLD if bold else FONT, size)
def center(draw, xy, text, size, fill='#292722'):
    draw.text(xy, text, font=font(size, True), fill=fill, anchor='mm')

cases = json.loads((ROOT / 'layouts.json').read_text())
# Thor is an existing two-display path, not the one-window resolver.
cases.append(dict(id='thor',title='Thor / physical dual screen',width=1000,height=950,layout={
 'World':dict(X=0,Y=0,Width=1000,Height=500),
 'Companion':dict(X=210,Y=530,Width=580,Height=420),
 'Movement':dict(X=0,Y=530,Width=200,Height=420),
 'Actions':dict(X=800,Y=530,Width=200,Height=420),
 'Hinge':dict(X=0,Y=500,Width=1000,Height=30)}))
NOTES['thor']='Physical top and bottom screens. Physical gamepad at the sides; no duplicate virtual pad.'

for c in cases:
    im=Image.new('RGB',(1600,1120),'#f7f4ec'); d=ImageDraw.Draw(im)
    d.text((70,42),'GUO / DEVICE LAYOUTS',font=font(22,True),fill='#76623c')
    d.text((70,87),c['title'],font=font(42,True),fill='#292722')
    d.text((70,145),'IMPLEMENTATION WIREFRAME · geometry from the layout policy',font=font(19),fill='#6c675e')
    scale=min(1380/c['width'],760/c['height'])
    ox=(1600-c['width']*scale)/2; oy=210
    d.rounded_rectangle((ox-15,oy-15,ox+c['width']*scale+15,oy+c['height']*scale+15),radius=20,fill='#282725')
    svg=[f'<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="1120" viewBox="0 0 1600 1120"><rect width="1600" height="1120" fill="#f7f4ec"/><g font-family="Segoe UI, sans-serif" fill="#292722"><text x="70" y="66" font-size="22">GUO / DEVICE LAYOUTS</text><text x="70" y="128" font-size="42">{escape(c["title"])}</text><text x="70" y="170" font-size="19">IMPLEMENTATION WIREFRAME</text>']
    for key in COLORS:
        r=c['layout'][key]
        if r['Width']<=0 or r['Height']<=0: continue
        x=ox+r['X']*scale; y=oy+r['Y']*scale; w=r['Width']*scale; h=r['Height']*scale
        d.rectangle((x,y,x+w,y+h),fill=COLORS[key],outline='#f7f4ec',width=3)
        label=LABELS[key]
        if c['id']=='thor' and key in ('Movement','Actions'): label='PHYSICAL PAD'
        size=min(25,max(12,int(w/(max(1,len(label))*.64))))
        if label: center(d,(x+w/2,y+h/2),label,size)
        svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" fill="{COLORS[key]}" stroke="#f7f4ec" stroke-width="3"/><text x="{x+w/2}" y="{y+h/2}" text-anchor="middle" dominant-baseline="middle" font-size="{size}">{label}</text>')
        if key=='World':
            cx=x+w/2; cy=y+h*.72; rad=9
            d.ellipse((cx-rad,cy-rad,cx+rad,cy+rad),fill='#4c6549')
            center(d,(cx,cy+26),'PLAYER',12)
    d.text((70,1020),NOTES[c['id']],font=font(21),fill='#514d44')
    d.text((70,1060),'Wireframes define pane placement. Real device photographs are reference material; hardware validation is separate.',font=font(18),fill='#76623c')
    svg.append(f'<text x="70" y="1040" font-size="20">{escape(NOTES[c["id"]])}</text></g></svg>')
    im.save(ROOT/(c['id']+'-wireframe.png'))
    (ROOT/(c['id']+'-wireframe.svg')).write_text(''.join(svg),encoding='utf-8')

sheet=Image.new('RGB',(2400,1830),'#f7f4ec'); d=ImageDraw.Draw(sheet)
d.text((50,30),'GUO · Device layout wireframes',font=font(42,True),fill='#292722')
for i,c in enumerate(cases):
    tile=Image.open(ROOT/(c['id']+'-wireframe.png')); tile.thumbnail((780,546))
    sheet.paste(tile,(20+(i%3)*795,110+(i//3)*565))
sheet.save(ROOT/'wireframes-overview.png')
cards=''.join(f'<article><h3>{escape(c["title"])}</h3><a href="{c["id"]}-wireframe.png"><img src="{c["id"]}-wireframe.png" alt="{escape(c["title"])} wireframe"></a><p>{NOTES[c["id"]]}</p><a href="{c["id"]}-wireframe.svg">Vector SVG</a></article>' for c in cases)
photos=json.loads((ROOT/'photos/sources.json').read_text(encoding='utf-8'))
photo_cards=''.join(f'<article><h3>{escape(p["device"])}</h3><a href="photos/{p["file"]}"><img class="photo" src="photos/{p["file"]}" alt="Real photograph of {escape(p["device"])}"></a><p>Photo: {escape(p["author"])} · <a href="{p["license_url"]}">{escape(p["license"])}</a> · <a href="{p["source_page"]}">Original</a></p></article>' for p in photos)
html="""<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>GUO · Mobile devices</title><style>
*{box-sizing:border-box}body{margin:0;background:#f7f4ec;color:#292722;font:18px/1.5 system-ui,sans-serif}main{max-width:1440px;margin:auto;padding:48px 28px}header{max-width:940px}h1{font:600 52px Georgia,serif;margin:12px 0}h2{margin-top:50px;font:36px Georgia,serif}.eyebrow{letter-spacing:.16em;color:#76623c;font-size:13px}a{color:#725016}img{max-width:100%;display:block}.photo{height:300px;width:100%;object-fit:contain;background:#e9e4d9}section.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(320px,100%),1fr));gap:24px}article{border:1px solid #d6cbb8;padding:18px;background:#fffdf7}article p{font-size:15px}.note{border-left:3px solid #9b803e;padding:12px 20px;background:#eee6d6}figure{margin:24px 0}.runtime{max-height:620px;margin:auto}figcaption{font-size:14px;margin-top:10px}@media print{main{padding:0}figure,article{break-inside:avoid}h1{font-size:32px}}</style><main><header><div class="eyebrow">GUO / MOBILE UI DEVELOPMENT</div><h1>Real devices. Reproducible layouts.</h1><p>Phones, foldables, tablets and Android handhelds. Explore the layout policy and test it with GUO Device Test Manager.</p><p class="note">The photographs below show real hardware with its original screen contents. They are references, not photographs of GUO running on those devices. GUO captures and layout diagrams are labeled separately.</p></header><h2>Real device photographs</h2><section class="grid">"""+photo_cards+"""</section><p><a href="photos/CREDITS.md">Photo credits and reuse licenses</a>. No generated device renders are used in this gallery.</p><h2>GUO in the Fold emulator</h2><figure><a href="guo-tabletop-capture.png"><img class="runtime" src="guo-tabletop-capture.png" alt="Actual GUO capture from the Fold emulator in tabletop mode"></a><figcaption>Actual emulator capture. World above the hinge; companion windows between movement and action controls below. This is not a physical-device photograph.</figcaption></figure><h2>Layout wireframes</h2><section class="grid">"""+cards+"""</section><h2>Developer testing</h2><p>Launch <strong>GUO Device Test Manager</strong> with <code>launchers\\android\\device_manager.bat</code>. Select a device, launch/apply its profile, then install and run GUO. Capture screenshots, short videos and diagnostics with profile metadata.</p><p>SDK profiles model display geometry and fold events. Display-size presets cover additional Android phones and handheld aspect ratios. Vendor firmware, performance and physical controller mappings still require hardware. Thor lower-screen simulation is not a two-display test.</p><p>Tent is selected manually in GUO on supported hardware. Android does not expose a universal tent posture. Rotation and fold transitions release held input before reflow.</p><p><a href="behavior.md">Behavior notes</a> · <a href="device-manager.md">Manager guide and coverage</a> · <a href="wireframes-overview.png">Wireframe contact sheet</a></p></main></html>"""
(ROOT/'index.html').write_text(html,encoding='utf-8')
print('Wrote nine PNG/SVG wireframes and real-photo mobile gallery.')
