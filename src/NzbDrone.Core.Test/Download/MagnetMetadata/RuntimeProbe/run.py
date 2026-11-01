"""Exercise the real resolver using synthetic loopback DNS/HTTP failures, never a real tracker.
Requires a local Linux .NET 10 SDK image and compiled candidate DLLs; no image pull.
Example: python run.py --candidate <DLL-directory> --sdk-image sha256:<digest> --output <new-directory>
No production configuration, sockets, published ports, or external network are used.
"""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--candidate", type=Path, required=True)
parser.add_argument("--sdk-image", required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
candidate = args.candidate.resolve(strict=True)
assert (candidate / "Sonarr.Core.dll").is_file()
assert args.sdk_image.startswith("sha256:"), "Use an immutable local SDK image ID"
output = args.output.resolve()
output.mkdir(parents=True, exist_ok=False)
shutil.copyfile(Path(__file__).with_name("Program.cs.template"), output / "Program.cs")
(output / "resolv.conf").write_text("nameserver 127.0.0.1\noptions timeout:1 attempts:1\n")
references = "".join(f'<Reference Include="{name}"><HintPath>/candidate/{name}.dll</HintPath></Reference>'
                     for name in ["Sonarr.Core", "Sonarr.Common", "NLog", "MonoTorrent", "MonoTorrent.Client"])
(output / "Probe.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
    '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
    '</PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App"/>' + references + '</ItemGroup></Project>')
command = ["docker", "run", "--rm", "--pull", "never", "--name", "sonarr-synthetic-resolver-" + uuid.uuid4().hex[:12],
    "--network", "none", "--read-only", "--memory", "1g", "--cpus", "2", "--pids-limit", "256",
    "--label", "com.centurylinklabs.watchtower.enable=false", "--tmpfs", "/tmp:rw,size=256m",
    "--mount", f"type=bind,source={output},target=/probe",
    "--mount", f"type=bind,source={candidate},target=/candidate,readonly",
    "--mount", f"type=bind,source={output / 'resolv.conf'},target=/etc/resolv.conf,readonly",
    "--entrypoint", "/bin/sh", args.sdk_image, "-c",
    "ulimit -c 0; cd /probe && dotnet restore Probe.csproj --source /tmp/empty -p:NuGetAudit=true -p:NuGetAuditMode=all && "
    "dotnet build Probe.csproj --no-restore -c Release && cp /candidate/*.dll bin/Release/net10.0/ && dotnet bin/Release/net10.0/Probe.dll"]
(output / "command.json").write_text(json.dumps(command, indent=2))
with (output / "probe.log").open("wb") as log:
    result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT)
if result.returncode:
    raise SystemExit(f"Probe failed ({result.returncode}); inspect {output / 'probe.log'}")
results = json.loads((output / "results.json").read_text())
assert results["passed"] and len(results["cases"]) == 3 and results["dnsRequests"] > 0
print(json.dumps(results, indent=2))
