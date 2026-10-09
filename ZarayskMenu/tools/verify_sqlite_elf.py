import struct, sys, hashlib, json, re
from pathlib import Path

def elf(path):
    data=Path(path).read_bytes()
    assert data[:6]==b'\x7fELF\x02\x01', 'Expected little-endian ELF64'
    assert struct.unpack_from('<H',data,18)[0]==62, 'Expected x86_64'
    off=struct.unpack_from('<Q',data,40)[0]
    size,count,namesidx=struct.unpack_from('<HHH',data,58)
    sections=[struct.unpack_from('<IIQQQQIIQQ',data,off+i*size) for i in range(count)]
    def content(s): return data[s[4]:s[4]+s[5]]
    def string(buf,n): return buf[n:buf.find(b'\0',n)].decode()
    names=content(sections[namesidx]); byname={string(names,s[0]):s for s in sections}
    strings=content(byname['.dynstr']); symbols=content(byname['.dynsym'])
    exports=set()
    for pos in range(0,len(symbols),24):
        name,info,other,index,value,n=struct.unpack_from('<IBBHQQ',symbols,pos)
        if index and info>>4 in (1,2): exports.add(string(strings,name))
    needed=[]
    dyn=content(byname['.dynamic'])
    for pos in range(0,len(dyn),16):
        tag,n=struct.unpack_from('<QQ',dyn,pos)
        if tag==1: needed.append(string(strings,n))
    versions=sorted(set(re.findall(rb'GLIBC_[0-9.]+',content(byname['.dynstr']))),key=lambda v:tuple(map(int,v[6:].split(b'.'))))
    return data,exports,needed,[v.decode() for v in versions]

old,oe,on,ov=elf(sys.argv[1]); new,ne,nn,nv=elf(sys.argv[2])
missing=sorted(x for x in oe-ne if x.startswith('sqlite3'))
assert not missing, missing
assert all(tuple(map(int,v[6:].split('.')))<=(2,17) for v in nv),nv
assert b'2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62' in new
source=Path(sys.argv[3]).read_bytes()
assert hashlib.sha3_256(source).hexdigest()=='28e484abdaa43630e34040ef6ed92be973a1ad54107803d8af5145b889c23ed7'
print(json.dumps(dict(version='3.53.3',source_sha3_verified=True,old_glibc=ov,new_glibc=nv,needed=nn,sqlite_exports=len([x for x in ne if x.startswith('sqlite3')]),missing_exports=missing,sha256=hashlib.sha256(new).hexdigest()),indent=2))
