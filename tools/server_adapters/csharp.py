# SPDX-License-Identifier: BSD-2-Clause
from pathlib import Path
from .adapter import literal as q


def render(data):
    lines = []
    for row in data.get('items', []):
        args = ','.join(q(row[k]) for k in ('identity','graphic','name','weight','movable'))
        lines.append(f'Items.Add({q(row["identity"])},new ItemDef({args}));')
    for row in data.get('loot', []):
        entries = ','.join(f'new LootEntry(Items[{q(e["item"])}],{q(e["chance"])},{e["min"]},{e["max"]})' for e in row['content']['entries'])
        lines.append(f'Loot.Add({q(row["identity"])},new LootEntry[] {{ {entries} }});')
    for row in data.get('creatures', []):
        c = row['content']; loot = f'Loot[{q(c["loot"])}]' if 'loot' in c else 'new LootEntry[0]'
        lines.append(f'Creatures.Add({q(row["identity"])},delegate {{ var m=new PackCreature({q(row["identity"])},AIType.AI_{"Animal" if c["ai"]=="animal" else "Melee"},{loot});')
        for prop,key in [('Name','name'),('Body','body'),('Hue','hue'),('BaseSoundID','sound'),('Fame','fame'),('Karma','karma'),('VirtualArmor','armor')]: lines.append(f'm.{prop}={q(c[key])};')
        for prop,key in [('Str','strength'),('Dex','dexterity'),('Int','intelligence')]: lines.append(f'm.Set{prop}({c[key]});')
        lines.append(f'm.HitsMaxSeed={c["hits"]};m.Hits={c["hits"]};')
        lines.append(f'm.SetDamage({c["damage_min"]},{c["damage_max"]});')
        for prop,key in [('Tactics','tactics'),('Wrestling','wrestling'),('MagicResist','resist')]: lines.append(f'm.SetSkill(SkillName.{prop},{q(c[key])});')
        lines.append('m.SetDamageType(ResistanceType.Physical,100); return m; });')
    for index,row in enumerate(data.get('tiles', [])):
        lines.append('{ int id='+str(row['id'])+'; if(id>=TileData.ItemTable.Length) throw new InvalidDataException("GUO tile exceeds server capacity"); ItemData tile=TileData.ItemTable[id];')
        for key,value in row['content'].items():
            prop={'name':'Name','flags':'Flags','height':'Height','weight':'Weight','layer':'Quality','animation':'Animation'}[key]
            val='unchecked((TileFlag)'+str(value)+'UL)' if key=='flags' else q(value)
            lines.append(f'tile.{prop}={val};')
        lines.append('Apply.Add(delegate { TileData.ItemTable[id]=tile; }); }')
    for row in data.get('maps', []):
        for b in row['content']['blocks']:
            land=','.join(f'new LandTile({c["graphic"]},{c["z"]})' for c in b['land'])
            statics='null'
            if 'statics' in b:
                cells=','.join(f'new StaticTile({c["graphic"]},{c["x"]},{c["y"]},{c["z"]},{c["hue"]})' for c in b['statics'])
                xs=','.join(str(c['x']) for c in b['statics']);ys=','.join(str(c['y']) for c in b['statics'])
                statics=f'StaticCells(new StaticTile[] {{{cells}}},new int[] {{{xs}}},new int[] {{{ys}}})'
            lines.append(f'StageBlock({row["facet"]},{b["x"]},{b["y"]},new LandTile[] {{{land}}},{statics});')
    for row in data.get('regions', []):
        c=row['content']; areas=','.join('new Rectangle3D('+','.join(str(a[k]) for k in ('x','y','z','width','height','depth'))+')' for a in c['areas'])
        lines.append(f'StageRegion({q(c["name"])},{c["facet"]},{c["priority"]},new Rectangle3D[] {{{areas}}},{q(c.get("enter_message",""))},{q(c.get("exit_message",""))},{row["music_id"]});')
    for row in data.get('decorations', []):
        placements=','.join(f'new Placement({q(c["id"])},{row["facet"]},'+','.join(str(c[k]) for k in ('graphic','x','y','z','hue'))+')' for c in row['items'])
        lines.append(f'StageDecorations({q(row["identity"])},new Placement[] {{{placements}}});')
    template=Path(__file__).with_name('templates').joinpath('GUOContent.cs.txt').read_text(encoding='utf-8-sig')
    return template.replace('__IDENTITY__',q(data['identity_hash'])).replace('__BUILD__','\n'.join('            '+line for line in lines))
