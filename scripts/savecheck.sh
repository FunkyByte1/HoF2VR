#!/usr/bin/env bash
# Abort loudly if the player's real Steam Cloud save differs from the pre-mod backup.
SAVE="$HOME/.local/share/Steam/userdata/STEAM_ACCOUNT_ID/456670/remote/player_data.profile"
EXPECT=85d108437458d710105850d04e28ef052005e68e   # the user's own play 2026-10-19 22:16 (their session 20261019-220935, after playtest 4; copy in backups/user-session-20261019-2209; was 8a4a4849... their 2026-10-18 14:50 save (third playtest, backups/user-session-20261018-1424), a5e4e0b2... their 2026-10-17 21:43 save, 62c2d185... their 09:44 save, 1e6f29cf... their 2026-10-12 20:49 save, 9c8007e8... their 2026-10-11 20:05 save, 5fe30636... their 14:48 death save, 1bffbe2c... their 07:51 save, before that 76e9e17c... 2026-10-10 18:53, 6897183a... pre-mod)
got=$(sha1sum < "$SAVE" | cut -d' ' -f1)
if [ "$got" != "$EXPECT" ]; then echo "!!! REAL SAVE CHANGED ($got) !!!" >&2; exit 3; fi
echo "real save OK (unchanged)"
