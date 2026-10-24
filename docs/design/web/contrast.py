#!/usr/bin/env python3
"""Check the T11.2 token palette using WCAG 2.2 sRGB contrast (stdlib only)."""

from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[3]
TOKENS = ROOT / 'apps/web/src/styles/tokens.css'


def declarations(body):
    """Read the deliberately flat declaration blocks; reject malformed/duplicate keys."""
    result = {}
    for statement in body.split(';'):
        if not statement.strip():
            continue
        match = re.fullmatch(r'\s*([\w-]+)\s*:\s*(.+?)\s*', statement, re.S)
        if not match:
            raise ValueError(f'Invalid declaration: {statement.strip()}')
        name, value = match.groups()
        if name in result:
            raise ValueError(f'Duplicate declaration: {name}')
        result[name] = value
    return result


def parse_themes(css):
    """Require one base block and both supported dark paths, not a general CSS parser."""
    css = re.sub(r'/\*.*?\*/', '', css, flags=re.S)
    patterns = {
        'light': r'(?m)^\s*:root\s*\{([^{}]*)\}',
        'explicit dark': r'''html\[data-theme=['"]dark['"]\]\s*\{([^{}]*)\}''',
        'system dark': (
            r'@media\s*\(prefers-color-scheme:\s*dark\)\s*\{\s*'
            r''':root:not\(\[data-theme=['"]light['"]\]\)\s*\{([^{}]*)\}'''
        ),
    }
    blocks = {}
    for name, pattern in patterns.items():
        matches = re.findall(pattern, css)
        if len(matches) != 1:
            raise ValueError(f'Expected exactly one {name} block; found {len(matches)}')
        blocks[name] = declarations(matches[0])
    explicit = blocks['explicit dark']
    system = blocks['system dark']
    if explicit != system:
        keys = sorted(key for key in explicit.keys() | system.keys()
                      if explicit.get(key) != system.get(key))
        raise ValueError('Dark blocks differ: ' + ', '.join(keys))
    if blocks['light'].get('color-scheme') != 'light' or explicit.get('color-scheme') != 'dark':
        raise ValueError('Expected color-scheme: light in base and dark in both dark blocks')
    return {'light': blocks['light'], 'dark': blocks['light'] | explicit}


def resolve(tokens, name, seen=()):
    if name in seen:
        raise ValueError('Cyclic token reference: ' + ' -> '.join((*seen, name)))
    if name not in tokens:
        raise ValueError(f'Missing token: {name}')
    value = tokens[name]
    reference = re.fullmatch(r'var\((--eg-[\w-]+)\)', value)
    if reference:
        return resolve(tokens, reference[1], (*seen, name))
    return value


def luminance(color):
    """IEC sRGB transfer function and WCAG relative-luminance coefficients."""
    if not re.fullmatch(r'#[0-9a-fA-F]{6}', color):
        raise ValueError(f'Expected opaque #RRGGBB, not {color}; alpha needs a backdrop')
    channels = [int(color[index:index + 2], 16) / 255 for index in (1, 3, 5)]
    linear = [value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4
              for value in channels]
    return sum(value * weight for value, weight in zip(linear, (0.2126, 0.7152, 0.0722)))


def contrast(foreground, background):
    darker, lighter = sorted((luminance(foreground), luminance(background)))
    return (lighter + 0.05) / (darker + 0.05)


def pairs():
    """Keep ordinary text at 4.5 even when also checked separately as a display heading."""
    checks = []
    for foreground in ('text', 'text-muted'):
        for background in ('bg', 'surface', 'surface-raised', 'accent-soft'):
            checks.append((foreground, background, 'text', 4.5))
    for background in ('bg', 'surface'):
        checks.append(('accent', background, 'link text', 4.5))
    checks.append(('on-accent', 'accent', 'button label', 4.5))
    for role in ('success', 'danger', 'warning', 'info', 'neutral', 'pending'):
        for background in (role + '-bg', 'surface'):
            checks.append((role + '-fg', background, 'status text', 4.5))
    checks.append(('accent-fg', 'accent-soft', 'accent text', 4.5))
    for background in ('surface', 'bg'):
        checks.append(('border-control', background, 'non-text border', 3.0))
    for background in ('surface', 'bg', 'surface-raised'):
        checks.append(('focus', background, 'non-text focus', 3.0))
    checks.append(('accent', 'surface', 'non-text control', 3.0))
    for background in ('pending-bg', 'surface', 'bg'):
        checks.append(('pending-border', background, 'non-text border', 3.0))
    # v2 page-header title: clamp(22px, 2vw, 28px), >=24px only at >=1200px viewport.
    # v2 22px semibold (600) KPI text is NOT assumed to satisfy the bold-text exception.
    checks.append(('text', 'bg', 'large display >=24px', 3.0))
    return checks


def main():
    try:
        themes = parse_themes(TOKENS.read_text(encoding='utf-8'))
        rows = []
        failures = 0
        for theme, tokens in themes.items():
            for foreground, background, usage, minimum in pairs():
                value = contrast(resolve(tokens, '--eg-' + foreground),
                                 resolve(tokens, '--eg-' + background))
                passed = value >= minimum  # Never use rounded values for the decision.
                failures += not passed
                rows.append(f'| {theme} | {foreground} / {background} | {usage} | '
                            f'{value:.2f} | {minimum:.1f} | {"PASS" if passed else "FAIL"} |')
        print('| Theme | Pair (--eg- prefix) | Usage | Ratio | Minimum | Result |')
        print('| --- | --- | --- | ---: | ---: | --- |')
        print('\n'.join(rows))
        print('\nDark declaration blocks: PASS (identical property names and values).')
        print('Excluded: --eg-scrim (#RRGGBBAA); backdrop alpha is not a text pair.')
        print(f'Result: {len(rows) - failures}/{len(rows)} pairs PASS; {failures} FAIL.')
        return 1 if failures else 0
    except (OSError, ValueError) as error:
        print(f'ERROR: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
