# console-titles.tsv.gz — data notice

`console-titles.tsv.gz` (game serial → title for PlayStation, PlayStation 2, PlayStation 3, PSP, GameCube and Wii;
product code → title for Nintendo 3DS; title ID → title for Xbox 360) is adapted from the Redump dats in
[libretro-database](https://github.com/libretro/libretro-database/tree/fbeefcb46c2e1b20a7e2945f34a694a41b2d6f90/metadat/redump)
(© the libretro team and contributors, data from [Redump](http://redump.org/)) and its No-Intro dats in
[libretro-database](https://github.com/libretro/libretro-database/tree/fbeefcb46c2e1b20a7e2945f34a694a41b2d6f90/metadat/no-intro)
(data from [No-Intro](https://no-intro.org/)), licensed under
[Creative Commons Attribution-ShareAlike 4.0 International](https://creativecommons.org/licenses/by-sa/4.0/).

Changes: only each game's serial, product code or title ID and name are kept (an Xbox 360 disc's title ID is computed
from its serial; its region and languages are dropped, one title ID being every region's); the name is split into the title, its region and
its language list, and loses its revision, disc number, EDC and eShop tags; one name is kept per serial (a retail
game's over a demo's, then the shortest).

This file, as adapted, is shared under the same CC BY-SA 4.0 license. That license covers this data file only —
not SaveLocker's code, which reads it as data and is licensed under the repository's `LICENSE`.

Rebuild it with `Update-ConsoleTitles.ps1` in this folder.
