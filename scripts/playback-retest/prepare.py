from pathlib import Path
import shutil, sqlite3, json, argparse
parser=argparse.ArgumentParser();parser.add_argument('--output',default='artifacts/sync-followup/desktop');args=parser.parse_args()
root=Path(__file__).resolve().parents[2];test=root/args.output;test.mkdir(parents=True,exist_ok=True)
for name in ('Watchroom.Core','Watchroom.Desktop'):
 shutil.copytree(root/'src'/name,test/'src'/name,dirs_exist_ok=True,ignore=shutil.ignore_patterns('bin','obj'))
(test/'Directory.Build.props').write_text((root/'Directory.Build.props').read_text().replace('$(MSBuildThisFileDirectory).nuget/packages',str(root/'.nuget/packages')))
driver=(Path(__file__).parent/'TestDriver.cs').read_text();(test/'src/Watchroom.Desktop/TestDriver.cs').write_text(driver)
p=test/'src/Watchroom.Desktop/MainWindow.xaml.cs';s=p.read_text(encoding='utf-8-sig')
needle='timer.Tick += (_, _) => PlaybackTick(); timer.Start();';assert s.count(needle)==1
s=s.replace(needle,needle+' Loaded += async (_, _) => await StartTestDriver();')
needle='private void PlaybackTick()\n    {';assert s.count(needle)==1
s=s.replace(needle,needle+'\n        TestDriverTick();')
p.write_text(s,encoding='utf-8-sig')
hashes=[]
import hashlib
for file in ['src/Watchroom.Desktop/MainWindow.xaml.cs','src/Watchroom.Core/Models.cs','src/Watchroom.Core/ServerClock.cs','src/Watchroom.Core/PlaybackSettling.cs','src/Watchroom.Core/PlaybackPosition.cs','src/Watchroom.Core/PlaybackDiagnostics.cs']:
 hashes.append({'path':file,'sha256':hashlib.sha256((root/file).read_bytes()).hexdigest()})
(test/'source-hashes.json').write_text(json.dumps(hashes,indent=2))
for mode in ('local','public'):
 for role in ('host','guest'):
  profile=test/f'{mode}-{role}';profile.mkdir(exist_ok=True)
  with sqlite3.connect(profile/'library.db') as db:
   db.executescript('CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY,path TEXT UNIQUE NOT NULL,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);')
   settings={'name':f'Sync followup {role}','artwork':'false','server':'http://localhost:5081' if mode=='local' else 'https://watchroom-rooms.svovoniks.chatgpt.site'}
   db.executemany('INSERT OR REPLACE INTO settings VALUES(?,?)',settings.items())
print(test)
