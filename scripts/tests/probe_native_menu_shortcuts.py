#!/usr/bin/env python3
"""Nonactivating AppKit NSMenu shortcut evidence; never reads the general clipboard."""
import argparse, os, pathlib, platform, subprocess, tempfile
p=argparse.ArgumentParser(description=__doc__)
p.add_argument('--build-only', action='store_true')
a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[2]
folder=pathlib.Path(tempfile.mkdtemp(prefix='mighty-native-menu-'))
binary=folder/'native-menu-probe'
env=os.environ.copy()
env.setdefault('DEVELOPER_DIR','/Library/Developer/CommandLineTools')
subprocess.run(['xcrun','swiftc','-parse-as-library','-target',platform.machine()+'-apple-macos14.0','-module-cache-path',str(folder/'module-cache'),str(root/'scripts/tests/native-menu-shortcuts-main.swift'),'-o',str(binary)],check=True,env=env)
if a.build_only:
 print(binary)
else:
 subprocess.run([str(binary)],check=True,timeout=15)
