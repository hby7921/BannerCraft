"""Run the pinned native Minecraft model compiler oracle and pure host snapshot checks."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parent.parent
scratch = root / '.build-game/generic-mob-oracle'
scratch.mkdir(parents=True, exist_ok=True)
jar = root / 'fabric/.gradle/loom-cache/minecraftMaven/net/minecraft/minecraft-merged-7e9a32a5b8/26.3/minecraft-merged-7e9a32a5b8-26.3.jar'
libraries = list((root / 'runtime/minecraft/PrismLauncher/libraries').rglob('*.jar'))
libraries += list((Path.home() / '.gradle/caches/modules-2/files-2.1/net.fabricmc.fabric-api').rglob('*.jar'))
cp = ';'.join(p.as_posix() for p in [jar, root / 'fabric/build/classes/java/main', *libraries])
jdk = root / '.toolchain/jdk25/jdk-25.0.4.1+1/bin'
args = scratch / 'javac.args'
sources=[root / 'tools/GenericMobModelOracle.java', root / 'tools/GenericMobServerLoadProbe.java', root / 'fabric/src/main/java/dev/mycraft/bridge/NativeMobModels.java']
args.write_text(f'-classpath\n"{cp}"\n-d\n"{scratch.as_posix()}"\n'+''.join(f'"{p.as_posix()}"\n' for p in sources), encoding='utf-8')
subprocess.run([str(jdk / 'javac.exe'), '@' + str(args)], check=True)
fixtures = root / 'tests/GenericMobVisuals/Fixtures'
args = scratch / 'java.args'
client_jar=root / 'runtime/minecraft/downloads/minecraft-26.3-client.jar'
args.write_text(f'-classpath\n"{scratch.as_posix()};{cp}"\nGenericMobModelOracle\n"{fixtures.as_posix()}"\n"{client_jar.as_posix()}"\n', encoding='utf-8')
subprocess.run([str(jdk / 'java.exe'), '@' + str(args)], check=True)
args=scratch / 'server.args'
args.write_text(f'-classpath\n"{scratch.as_posix()};{cp}"\nGenericMobServerLoadProbe\n',encoding='utf-8')
subprocess.run([str(jdk / 'java.exe'),'@'+str(args)],check=True)
subprocess.run(['dotnet', 'run', '--project', str(root / 'tests/GenericMobVisuals/MycraftGenericMobVisualTests.csproj')], check=True)
