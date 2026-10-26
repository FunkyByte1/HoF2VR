#!/usr/bin/env bash
# candidate 8's shield regression: chain_pf.sh's C, F and G blocks, 2 rounds, each block its own short run (its own
# reservation, released between blocks: env 1 needs the slot too) -> logs/pf17c_<block><round>.log
H=/home/bara/Projects/Flat2VR-env2/HandOfFate2
cd "$H" || exit 1
for r in 1 2; do for b in C F G; do
  NAME=pf17c_$b$r ROUNDS=1 BLOCKS=$b PRE=0 FOCUS=0 bash scripts/chains/pf/chain_pf.sh
  sleep 20
done; done
echo "=== all done $(date +%T)"
