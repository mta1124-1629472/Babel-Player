import os

def fix_file(filename):
    with open(filename, 'r') as f:
        lines = f.readlines()

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

    with open(filename, 'w') as f:
        f.write(''.join(out))

fix_file('BabelPlayer.Tests/ManagedVenvHostManagerTests.cs')
fix_file('Services/ManagedVenvHostManager.cs')
