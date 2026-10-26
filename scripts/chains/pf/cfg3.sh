#!/usr/bin/env bash
# after cfg2: the new DLL (crit slash 0.8 m out, the fight's start) - C, F, G twice, each its own short run, then look.sh
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
for r in 1 2; do for b in C F G; do
  NAME=pf17d_$b$r ROUNDS=1 BLOCKS=$b PRE=0 FOCUS=0 bash scripts/chains/pf/chain_pf.sh
  sleep 20
done; done
bash scripts/chains/pf/look.sh
echo "=== all done $(date +%T)"
