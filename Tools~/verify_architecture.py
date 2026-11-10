#!/usr/bin/env python3
"""Check package boundaries without Unity; this is not a C# compiler.

Source ownership follows the nearest assembly definition/reference. Comments and
string literals are removed before checking forbidden engine/physics identifiers.
External assembly references are intentionally outside the local dependency DAG.
Nested optional UPM packages are checked relative to their own package roots.
"""

import argparse
import json
import re
import sys
from pathlib import Path


LITERALS = re.compile(r'//[^\n]*|/\*.*?\*/|@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', re.S)
ENGINE = re.compile(r'\b(?:UnityEngine|UnityEditor)\b')
PHYSICS = re.compile(r'\b(?:(?:Physics|Collider|BoxCollider|CapsuleCollider|Rigidbody|RaycastHit|Collision)(?:2D)?|'
                     r'SphereCollider|MeshCollider|TerrainCollider|WheelCollider|ContactFilter2D|QueryTriggerInteraction)\b')
FOUNDATIONS = {
    'ZFramework.GAS': set(),
    'ZFramework.GAS.Unity': {'ZFramework.GAS'},
    'ZFramework.GAS.Editor': {'ZFramework.GAS', 'ZFramework.GAS.Unity'},
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    root = parser.parse_args().root.resolve()
    errors = []

    def fail(path, message):
        errors.append(f'{path.relative_to(root)}: {message}')

    paths = [p for p in root.rglob('*')
             if not any(part.startswith('.') or part == '__pycache__' for part in p.relative_to(root).parts)]
    files = [p for p in paths if p.is_file()]

    def ignored(path, package=root):
        return any(part.endswith('~') for part in path.relative_to(package).parts)

    def read_json(path):
        try:
            return json.loads(path.read_text(encoding='utf-8-sig'))
        except (OSError, ValueError) as error:
            fail(path, str(error))
            return {}

    packages = {path.parent: read_json(path) for path in files if path.name == 'package.json'}

    def package_root(path):
        for directory in (path, *path.parents):
            if directory in packages:
                return directory
        return root

    for package in packages:
        if package != root and not ignored(package):
            fail(package, 'nested package must be opt-in under a ~ folder')
        if package != root and packages[package]['name'] in packages[root].get('dependencies', {}):
            fail(root / 'package.json', 'base package must not depend on an optional integration')
    if any(name.startswith('com.esotericsoftware.spine.') for name in packages[root].get('dependencies', {})):
        fail(root / 'package.json', 'base package must not depend on Spine')

    assemblies = {}
    by_folder = {}
    by_guid = {}
    for path in files:
        if path.suffix != '.asmdef':
            continue
        definition = read_json(path)
        name = definition.get('name')
        if not name:
            fail(path, 'assembly name is missing')
            continue
        if name in assemblies:
            fail(path, f'duplicate assembly name {name}')
        assemblies[name] = (path, definition)
        if path.parent in by_folder:
            fail(path, 'multiple assembly definitions in one folder')
        by_folder[path.parent] = name
        meta = Path(str(path) + '.meta')
        if meta.exists():
            match = re.search(r'^guid:\s*(\w+)', meta.read_text(), re.M)
            if match:
                by_guid[match[1]] = name

    def resolve(reference):
        return by_guid.get(reference[5:], reference) if reference.startswith('GUID:') else reference

    def test_assembly(definition):
        return 'TestAssemblies' in definition.get('optionalUnityReferences', [])

    for path in files:
        if path.suffix == '.asmref':
            reference = resolve(read_json(path).get('reference', ''))
            if reference not in assemblies:
                fail(path, f'cannot resolve assembly reference {reference}')
            else:
                by_folder[path.parent] = reference

    def owner(path):
        for directory in path.parents:
            if directory in by_folder:
                return by_folder[directory]
            if directory == root:
                break
        return None

    graph = {}
    external = set()
    for name, (path, definition) in assemblies.items():
        references = {resolve(item) for item in definition.get('references', [])}
        package = package_root(path)
        optional = ignored(path, package) or test_assembly(definition)
        graph[name] = references & assemblies.keys()
        external.update(references - assemblies.keys())
        if package == root and not optional and any(reference.startswith('spine-') for reference in references):
            fail(path, 'base package must not reference Spine')
        for reference in references:
            if reference.startswith('ZFramework.GAS') and reference not in assemblies:
                fail(path, f'missing package assembly {reference}')
        for reference in graph[name]:
            target_path, target = assemblies[reference]
            target_package = package_root(target_path)
            if not optional and (ignored(target_path, target_package) or test_assembly(target)):
                fail(path, f'installed assembly depends on optional source {reference}')
            if not optional and package != target_package and packages[target_package]['name'] not in packages[package].get('dependencies', {}):
                fail(path, f'package dependency is not declared for {reference}')
            if definition.get('includePlatforms') != ['Editor'] and target.get('includePlatforms') == ['Editor']:
                fail(path, f'player assembly depends on editor assembly {reference}')
            if name in FOUNDATIONS and reference not in FOUNDATIONS[name]:
                fail(path, f'foundation depends on extension {reference}')
            if definition.get('noEngineReferences') and not target.get('noEngineReferences'):
                fail(path, f'engine-free assembly depends on engine assembly {reference}')

    visited = set()
    active = []

    def visit(name):
        if name in active:
            fail(assemblies[name][0], 'assembly cycle: ' + ' -> '.join(active[active.index(name):] + [name]))
            return
        if name in visited:
            return
        active.append(name)
        for dependency in sorted(graph[name]):
            visit(dependency)
        active.pop()
        visited.add(name)

    for name in sorted(graph):
        visit(name)
    for name in FOUNDATIONS:
        if name not in assemblies:
            fail(root / 'package.json', f'missing foundation assembly {name}')
    core = assemblies.get('ZFramework.GAS')
    if core and not core[1].get('noEngineReferences'):
        fail(core[0], 'Core must set noEngineReferences=true')

    source_count = 0
    for path in files:
        if path.suffix != '.cs':
            continue
        assembly = owner(path)
        if not assembly:
            if not ignored(path):
                fail(path, 'installed C# source has no assembly definition')
            continue
        source_count += 1
        code = LITERALS.sub(lambda match: '\n' * match.group().count('\n'), path.read_text(encoding='utf-8-sig'))
        if package_root(path) == root and not ignored(path) and re.search(r'\bSpine\b', code):
            fail(path, 'base package must not reference Spine types')
        forbidden = ENGINE if assemblies[assembly][1].get('noEngineReferences') else PHYSICS if assembly == 'ZFramework.GAS.Unity' else None
        if forbidden:
            for match in forbidden.finditer(code):
                fail(path, f'line {code.count(chr(10), 0, match.start()) + 1}: {assembly} uses {match.group()}')

    for path in paths:
        package = package_root(path)
        if path == package or ignored(path, package) or path.suffix == '.meta':
            continue
        if not Path(str(path) + '.meta').is_file():
            fail(path, 'missing Unity .meta file')

    manifest = packages[root]
    if (root / 'Samples').exists():
        fail(root / 'Samples', 'samples must be opt-in under Samples~')
    samples = manifest.get('samples', [])
    if (root / 'Samples~').exists() and not samples:
        fail(root / 'package.json', 'Samples~ exists but package samples are not declared')
    for sample in samples:
        path = Path(sample.get('path', ''))
        if path.is_absolute() or '..' in path.parts or not path.parts or path.parts[0] != 'Samples~':
            fail(root / 'package.json', f'invalid optional sample path {path}')
        elif not (root / path).is_dir():
            fail(root / 'package.json', f'missing sample directory {path}')

    if errors:
        print('\n'.join(errors), file=sys.stderr)
        print(f'FAIL: {len(errors)} architecture violations.', file=sys.stderr)
        return 1
    print(f'PASS: {len(packages)} packages, {len(assemblies)} assembly definitions, {source_count} C# sources; boundaries, DAG, metadata and optional samples verified.')
    if external:
        print('External references (not inspected): ' + ', '.join(sorted(external)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
