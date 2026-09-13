# Normalize retained source TIFFs to PNG working inputs. Requires Python, Pillow and NumPy.
# ExtraSamples=0 is a data channel; mark it unassociated only in the decoder buffer.
# Never modify source TIFFs. Height conversion maps the full unsigned 16-bit range.
from pathlib import Path
from PIL import Image,ImageStat
import io,struct,json,re
root=Path(__file__).resolve().parent
repo=root.parents[3]
manifest=json.loads((root/'DonorManifest.json').read_text())
for entry in manifest['materials']:
 sourceRoot=root/'Sources'/entry['name']
 for role in ['colour','normal','mask','height']:
  if not entry[role]:continue
  suffix={'colour':['BaseColor','Albedo'],'normal':['Normal'],'mask':['MaskMap'],'height':['Height']}[role]
  p=next(sourceRoot/(entry['name']+'_'+s+'.tif') for s in suffix if (sourceRoot/(entry['name']+'_'+s+'.tif')).exists())
  raw=bytearray(p.read_bytes());endian='<' if raw[:2]==b'II' else '>'
  offset=struct.unpack_from(endian+'I',raw,4)[0];count=struct.unpack_from(endian+'H',raw,offset)[0]
  for i in range(count):
   pos=offset+2+i*12;tag,kind,n=struct.unpack_from(endian+'HHI',raw,pos)
   if tag==338 and kind==3 and n==1 and struct.unpack_from(endian+'H',raw,pos+8)[0]==0:
    struct.pack_into(endian+'H',raw,pos+8,2)
  with Image.open(io.BytesIO(raw)) as im:
   if role=='height':
    import numpy as np
    pixels=np.array(im,dtype=np.float64)
    scale=65535.0 if im.mode.startswith('I;16') else 255.0
    im=Image.fromarray(np.clip(np.rint(pixels*255/scale),0,255).astype('uint8'),'L')
   else: im=im.convert('RGBA')
   im.save(repo/entry[role])
   if role in ('mask','height'): print(entry['name'],role,im.mode,im.getextrema(),flush=True)
 donor=Path(entry['source'])
 terrainRoot=(donor.parent if 'Source Assets' in str(donor) else donor.parents[1]/'Layers')
 terrain=terrainRoot/(entry['name']+'.terrainlayer')
 if terrain.exists():
  txt=terrain.read_text()
  for key,field in [('maskRemapMin','m_MaskMapRemapMin'),('maskRemapMax','m_MaskMapRemapMax')]:
   match=re.search(field+r': \{x: ([^,]+), y: ([^,]+), z: ([^,]+), w: ([^}]+)',txt)
   if match:entry[key]=dict(zip(['x','y','z','w'],map(float,match.groups())))
 entry['conversionVersion']='tiff-rgba-height16-v3'
(root/'DonorManifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
