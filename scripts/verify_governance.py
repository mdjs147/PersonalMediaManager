#!/usr/bin/env python3
"""One failure-sensitive governance verification entry; no product acceptance."""
import argparse
from pathlib import Path
import subprocess
import sys
import unittest
import pmm_governance as g

def main(argv=None):
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--adoption-bootstrap',action='store_true');a=p.parse_args(argv)
    root=g.v.root_path(g.ROOT)
    try:
        g.runtime(root);g.baseline(root);g.public_tree(root);g.documentation(root)
        if a.adoption_bootstrap:
            g.branch_owner(root,g.AUTHOR);g.cards(root);g.scoped(root)
            g.context(root,'adoption',allow_pending=True)
        else:
            g.context(root,'adoption')
        for path in ['docs/agents/readiness','tests/governance']:
            count=unittest.TestLoader().discover(str(root/path),pattern='test_*.py').countTestCases()
            g.need(count>0,'Required suite is empty: '+path)
            result=subprocess.run([sys.executable,'-m','unittest','discover','-s',path,'-p','test_*.py','-v'],cwd=root)
            g.need(result.returncode==0,'Required verification failed: '+path)
        g.need(subprocess.run(['git','diff','--check'],cwd=root).returncode==0,'Whitespace validation failed')
        print('PMM_GOVERNANCE_VERIFIED; product implementation remains absent and blocked')
        return 0
    except (g.v.Invalid,OSError) as exc:
        print('PMM_VERIFICATION_REJECTED: '+str(exc),file=sys.stderr);return 1
if __name__=='__main__':sys.exit(main())
