# Relive The Past

This plugin gives a player **one** second chance if that player dies during the first **30 seconds** of a round. The second chance is a delayed respawn as either a Class D or Scientist.

This plugin is for preventing most people die in first 30 seconds by gambling in my server. 

## Behavior

| Rule | Default | Result |
|---|---:|---|
| Early-death window | `30` seconds | A death at or before 30 seconds queues one second chance. |
| Chances per player per round | `1` | Subsequent deaths never queue another respawn. |
| Respawn delay | `3` seconds | Gives the death state time to settle before role reassignment. |
| Second-chance role | Random Class D / Scientist | The role can be fixed in `config.yml`. |
| Burst limit | `8` respawns per frame | Avoids a single-frame role-change spike after a mass casualty. |
| Alpha-warhead safety | Enabled | Pending respawns are cancelled while the sequence is active or the warhead has detonated. |

## Installation

Install `ReliveThePast.dll`, put it in LabAPI/plugins/global

