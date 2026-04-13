import os
import sys
from datetime import datetime

def validate_conflict_markers(lines):
    """Validate that conflict markers are properly paired."""
    i = 0
    while i < len(lines):
        if lines[i].startswith('<<<<<<< HEAD'):
            # Find matching =======
            j = i + 1
            while j < len(lines) and not lines[j].startswith('======='):
                j += 1
            if j >= len(lines):
                return False, f"Line {i+1}: Found '<<<<<<< HEAD' without matching '======='"
            # Find matching >>>>>>>
            k = j + 1
            while k < len(lines) and not lines[k].startswith('>>>>>>>'):
                k += 1
            if k >= len(lines):
                return False, f"Line {i+1}: Found '<<<<<<< HEAD' and '=======' without matching '>>>>>>>'"
            i = k + 1
        else:
            i += 1
    return True, None

def fix_file(filename, apply=False):
    """
    Process a file containing Git conflict markers.

    Args:
        filename: Path to the file to process
        apply: If True, write the changes; if False, perform dry-run

    Returns:
        The resolved content as a string
    """
    if not os.path.exists(filename):
        print(f"ERROR: File not found: {filename}", file=sys.stderr)
        return None

    with open(filename, 'r') as f:
        lines = f.readlines()

    # Validate conflict markers first
    valid, error = validate_conflict_markers(lines)
    if not valid:
        print(f"ERROR in {filename}: {error}", file=sys.stderr)
        return None

    out = []
    i = 0
    while i < len(lines):
        line = lines[i]
        if line.startswith('<<<<<<< HEAD'):
            # Skip until =======
            i += 1
            while i < len(lines) and not lines[i].startswith('======='):
                i += 1
            # Now we are at =======, keep everything until >>>>>>>
            i += 1
            while i < len(lines) and not lines[i].startswith('>>>>>>>'):
                out.append(lines[i])
                i += 1
            i += 1 # skip >>>>>>>
            continue
        else:
            out.append(line)
            i += 1

    resolved_content = ''.join(out)

    if apply:
        # Create timestamped backup
        timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
        backup_path = f"{filename}.backup_{timestamp}"
        with open(backup_path, 'w') as f:
            f.write(''.join(lines))
        print(f"Created backup: {backup_path}")

        # Write resolved content
        with open(filename, 'w') as f:
            f.write(resolved_content)
        print(f"Resolved: {filename}")
    else:
        print(f"DRY-RUN: {filename} — {len([l for l in lines if l.startswith('<<<<<<< HEAD')])} conflict(s) detected")

    return resolved_content

def main():
    import argparse
    parser = argparse.ArgumentParser(description='Resolve Git conflict markers by keeping incoming changes')
    parser.add_argument('files', nargs='*', help='Files to process (default: hardcoded list)')
    parser.add_argument('--apply', action='store_true', help='Apply changes (default is dry-run)')
    args = parser.parse_args()

    files = args.files if args.files else [
        'BabelPlayer.Tests/ManagedVenvHostManagerTests.cs',
        'Services/ManagedVenvHostManager.cs'
    ]

    for filename in files:
        fix_file(filename, apply=args.apply)

if __name__ == '__main__':
    main()