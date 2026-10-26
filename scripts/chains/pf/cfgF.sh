#!/usr/bin/env bash
# after cfg3: the F block twice more (linecut's setup: the others 6 m away, a whiff set up again)
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
while ! grep -q "all done" logs/pf/cfg3.out; do sleep 10; done
for r in 1 2; do NAME=pf17e_F$r ROUNDS=1 BLOCKS=F PRE=0 FOCUS=0 bash scripts/chains/pf/chain_pf.sh; sleep 20; done
echo "=== all done $(date +%T)"
