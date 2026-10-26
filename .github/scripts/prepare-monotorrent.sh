#!/usr/bin/env bash
set -euo pipefail
umask 077

# This is an archived binary, not a claim of reproducibility from source.
dependency_commit='53a58c56d3bf847b97f4b577d9b575c642369188'
package_name='MonoTorrent.3.0.2-sonarrsecurity.1.rev0002.nupkg'
expected_sha256='039eda4b39cb2d17c3e187ada0f5c4cd12852ec51c8ea968904024217aa2fcfa'
package_url="https://raw.githubusercontent.com/gaboy498-byte/Sonarr-Pro-Dependencies/${dependency_commit}/${package_name}"

repo_root="$(git rev-parse --show-toplevel)"
provision_root="$(mktemp -d "${RUNNER_TEMP:?RUNNER_TEMP must be set}/monotorrent.XXXXXXXX")"
download="${provision_root}/package.download"

if ! curl --fail --location --silent --show-error --proto '=https' --proto-redir '=https' \
    --tlsv1.2 --retry 3 --connect-timeout 20 --max-time 120 \
    --output "$download" "$package_url"; then
    echo 'MonoTorrent package download failed.' >&2
    exit 1
fi

if ! printf '%s  %s\n' "$expected_sha256" "$download" | sha256sum --check --status; then
    echo 'MonoTorrent package checksum verification failed.' >&2
    exit 1
fi

# Git Bash is also useful for local verification with the Windows .NET SDK.
native_path() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi
}

python3 - "$(native_path "$download")" "$(native_path "$provision_root")" \
    "$(native_path "$repo_root")" "$package_name" <<'PY'
from pathlib import Path
import hashlib
import os
import sys
import xml.etree.ElementTree as ET
import zipfile

download, root, repo = map(Path, sys.argv[1:4])
package_name = sys.argv[4]
assemblies = (
    'MonoTorrent', 'MonoTorrent.BEncoding', 'MonoTorrent.Factories',
    'MonoTorrent.Messages', 'MonoTorrent.PieceWriter', 'MonoTorrent.Dht',
    'MonoTorrent.Connections', 'MonoTorrent.PiecePicking',
    'MonoTorrent.PortForwarding', 'MonoTorrent.Trackers', 'MonoTorrent.Client',
)
expected_entries = {
    '_rels/.rels', 'MonoTorrent.nuspec', 'LICENSE', '[Content_Types].xml',
    'package/services/metadata/core-properties/64e3f6a17a59480eb6f4bb3640a4026e.psmdcp',
} | {f'lib/net6.0/{name}.{extension}' for name in assemblies for extension in ('dll', 'xml')}

with zipfile.ZipFile(download) as archive:
    if len(archive.namelist()) != len(expected_entries) or set(archive.namelist()) != expected_entries:
        sys.exit('MonoTorrent package contents do not match the validated archive.')
    if archive.testzip() is not None:
        sys.exit('MonoTorrent package contents failed ZIP integrity verification.')
    ns = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
    metadata = ET.fromstring(archive.read('MonoTorrent.nuspec')).find('n:metadata', ns)
    if metadata is None or metadata.findtext('n:id', namespaces=ns) != 'MonoTorrent' or metadata.findtext('n:version', namespaces=ns) != '3.0.2-sonarrsecurity.1.rev0002':
        sys.exit('MonoTorrent package identity/version verification failed.')
    if hashlib.sha256(archive.read('LICENSE')).hexdigest() != 'b44826a971bc09102317a4b8897a14a073406e54476248a79777d4f997bbd945':
        sys.exit('MonoTorrent MIT license verification failed.')

# Fail closed if the project's approved feeds change; review their mappings first.
sources = {
    'nuget.org': 'https://api.nuget.org/v3/index.json',
    'Mono.Posix.NETStandard': 'https://pkgs.dev.azure.com/Servarr/Servarr/_packaging/Mono.Posix.NETStandard/nuget/v3/index.json',
}
original = ET.parse(repo / 'src/NuGet.Config').getroot().find('packageSources')
if original is None or {s.attrib['key']: s.attrib['value'] for s in original.findall('add')} != sources:
    sys.exit('NuGet feeds changed; review the provisioning source mappings.')

feed = root / 'feed'
cache = root / 'packages'
feed.mkdir()
cache.mkdir()
config = ET.Element('configuration')
package_sources = ET.SubElement(config, 'packageSources')
ET.SubElement(package_sources, 'clear')
for key, value in {**sources, 'hardened-monotorrent': feed.as_posix()}.items():
    ET.SubElement(package_sources, 'add', key=key, value=value)
mapping = ET.SubElement(config, 'packageSourceMapping')
ET.SubElement(mapping, 'clear')
for key, pattern in [('nuget.org', '*'), ('Mono.Posix.NETStandard', 'Mono.Posix.NETStandard'), ('hardened-monotorrent', 'MonoTorrent')]:
    source = ET.SubElement(mapping, 'packageSource', key=key)
    ET.SubElement(source, 'package', pattern=pattern)
audit_sources = ET.SubElement(config, 'auditSources')
ET.SubElement(audit_sources, 'clear')
ET.SubElement(audit_sources, 'add', key='nuget.org', value=sources['nuget.org'])
config_path = root / 'NuGet.Config'
ET.indent(config)
ET.ElementTree(config).write(config_path, encoding='utf-8', xml_declaration=True)
os.replace(download, feed / package_name)

# Export nothing until every verification and configuration step has succeeded.
values = {'NUGET_PACKAGES': cache.as_posix(), 'MONOTORRENT_NUGET_CONFIG': config_path.as_posix()}
if any('\n' in value or '\r' in value for value in values.values()):
    sys.exit('Unsupported newline in provisioning path.')
for variable in ('GITHUB_ENV', 'GITHUB_OUTPUT'):
    if os.environ.get(variable):
        with open(os.environ[variable], 'a', encoding='utf-8') as output:
            for key, value in values.items():
                output.write(f'{key}={value}\n')
print('Verified MonoTorrent 3.0.2-sonarrsecurity.1.rev0002; isolated feed and cache prepared.')
PY
