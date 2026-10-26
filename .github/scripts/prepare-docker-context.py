#!/usr/bin/env python3
"""Stage tracked build inputs and the already verified public dependency only."""

import hashlib
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def prepare_context():
    repo = Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], text=True).strip()).resolve()
    # This exact Community grant alone is authorized for public redistribution.
    approved_license_name = 'licenses/SixLabors/sixlabors.lic'
    approved_license_sha256 = 'df6970833628b596450efc421870c074ee6dbf0f026bf4ad2b656dfb1d13230b'
    package_name = 'MonoTorrent.3.0.2-sonarrsecurity.1.rev0002.nupkg'
    expected_sha = '039eda4b39cb2d17c3e187ada0f5c4cd12852ec51c8ea968904024217aa2fcfa'
    config = ET.parse(os.environ['MONOTORRENT_NUGET_CONFIG'])
    if [section.tag for section in config.getroot()] != ['packageSources', 'packageSourceMapping', 'auditSources']:
        raise ValueError('Unexpected NuGet configuration sections')
    sources = {source.attrib['key']: source for source in config.getroot().find('packageSources').findall('add')}
    if set(sources) != {'nuget.org', 'Mono.Posix.NETStandard', 'hardened-monotorrent'}:
        raise ValueError('Unexpected NuGet feeds')
    if sources['nuget.org'].attrib['value'] != 'https://api.nuget.org/v3/index.json' or sources['Mono.Posix.NETStandard'].attrib['value'] != 'https://pkgs.dev.azure.com/Servarr/Servarr/_packaging/Mono.Posix.NETStandard/nuget/v3/index.json':
        raise ValueError('Unexpected NuGet feed URLs')
    mappings = {source.attrib['key']: [p.attrib['pattern'] for p in source] for source in config.getroot().find('packageSourceMapping').findall('packageSource')}
    if mappings != {'nuget.org': ['*'], 'Mono.Posix.NETStandard': ['Mono.Posix.NETStandard'], 'hardened-monotorrent': ['MonoTorrent']}:
        raise ValueError('Unexpected NuGet package source mappings')
    audit = config.getroot().find('auditSources')
    if len(audit.findall('add')) != 1 or audit.find('add').attrib != {'key': 'nuget.org', 'value': 'https://api.nuget.org/v3/index.json'}:
        raise ValueError('Unexpected NuGet audit source')
    package = Path(sources['hardened-monotorrent'].attrib['value']) / package_name
    if package.is_symlink() or not package.is_file() or hashlib.sha256(package.read_bytes()).hexdigest() != expected_sha:
        raise ValueError('MonoTorrent package missing or checksum mismatch')
    with zipfile.ZipFile(package) as archive:
        ns = {'n': 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
        metadata = ET.fromstring(archive.read('MonoTorrent.nuspec')).find('n:metadata', ns)
        if metadata.findtext('n:id', namespaces=ns) != 'MonoTorrent' or metadata.findtext('n:version', namespaces=ns) != '3.0.2-sonarrsecurity.1.rev0002':
            raise ValueError('MonoTorrent identity mismatch')
        notice = archive.read('LICENSE')

    # Use current contents of tracked files, including reviewed local modifications.
    # Do not archive HEAD or recursively copy the working tree and its private files.
    root_files = {'Dockerfile', '.dockerignore', '.editorconfig', 'global.json', 'package.json', 'yarn.lock', '.yarnrc', 'tsconfig.json', 'LICENSE.md'}
    source_roots = {'src', 'frontend', 'Logo'}
    entries = subprocess.check_output(['git', 'ls-files', '--stage', '-z']).decode('utf-8').split('\0')
    selected = []
    for entry in filter(None, entries):
        metadata, name = entry.split('\t', 1)
        mode, _, stage = metadata.split()
        path = PurePosixPath(name)
        if name == approved_license_name and (stage != '0' or mode not in {'100644', '100755'}):
            raise ValueError('Approved public license has an unmerged/symlink Git entry')
        if name not in root_files and name != 'docker/entrypoint.sh' and path.parts[0] not in source_roots:
            continue
        if stage != '0' or mode not in {'100644', '100755'}:
            raise ValueError(f'Unmerged file, submodule or symlink in Docker inputs: {name}')
        lower_parts = [part.lower() for part in path.parts]
        sensitive_suffixes = ('.lic', '.pem', '.key', '.pfx', '.p12', '.db', '.db3', '.sqlite', '.sqlite3', '.nupkg', '.log')
        if any(part.startswith('.env') or part in {'local-nuget', 'node_modules', 'obj', 'bin', '_temp', '_output', '.git'} for part in lower_parts) or name.lower().endswith(sensitive_suffixes):
            raise ValueError(f'Unexpected sensitive/generated Docker input: {name}')
        source = repo / name
        if any(parent.is_symlink() for parent in [source, *source.parents] if parent != repo) or not source.resolve(strict=True).is_relative_to(repo):
            raise ValueError(f'Escaping path or symlink in Docker inputs: {name}')
        selected.append((name, source))
    if not root_files.issubset({name for name, _ in selected}) or any(not any(name.startswith(prefix + '/') for name, _ in selected) for prefix in source_roots):
        raise ValueError('Required tracked Docker build inputs are missing')
    if 'docker/entrypoint.sh' not in {name for name, _ in selected}:
        raise ValueError('Tracked Docker entrypoint is missing')

    # Permit this one new/uncommitted file for local review as well as tracked CI.
    # Never broaden the source roots or the general rejection of .lic files.
    license_source = repo / approved_license_name
    if any(parent.is_symlink() for parent in [license_source, *license_source.parents] if parent != repo) or not license_source.resolve(strict=True).is_relative_to(repo):
        raise ValueError('Approved public license path is a symlink or escapes the repository')
    if hashlib.sha256(license_source.read_bytes()).hexdigest() != approved_license_sha256:
        raise ValueError('Approved public license integrity mismatch')
    selected.append((approved_license_name, license_source))

    context = Path(tempfile.mkdtemp(prefix='sonarr-docker-', dir=os.environ['RUNNER_TEMP'])).resolve()
    for name, source in selected:
        target = context / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    if hashlib.sha256((context / approved_license_name).read_bytes()).hexdigest() != approved_license_sha256:
        raise ValueError('Staged public license integrity mismatch')
    dependencies = context / '.ci-dependencies'
    dependencies.mkdir()
    shutil.copyfile(package, dependencies / package_name)
    if hashlib.sha256((dependencies / package_name).read_bytes()).hexdigest() != expected_sha:
        raise ValueError('Staged MonoTorrent checksum mismatch')
    (dependencies / 'MonoTorrent.LICENSE').write_bytes(notice)
    sources['hardened-monotorrent'].attrib['value'] = '/run/sonarr-dependencies'
    config.write(dependencies / 'NuGet.Config', encoding='utf-8', xml_declaration=True)
    if '\n' in str(context) or '\r' in str(context):
        raise ValueError('Unsupported newline in Docker context path')
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
            output.write(f'context={context.as_posix()}\n')
    print(f'Prepared tracked Docker build context ({len(selected)} source files); verified dependency included.')


if __name__ == '__main__':
    prepare_context()
