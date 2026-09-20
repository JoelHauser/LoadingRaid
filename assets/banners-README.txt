Deploy Screen Overhaul -- the map art.
======================================

These folders already have pictures in them: ten for most maps, from the Escape
from Tarkov wiki's "Showcase" set, which is BSG's own capture of each location at
1920x1080. You do not have to do anything to use them.

One picture fills the screen while a raid loads. Each time you load a map the next
picture in its folder is used, wrapping round at the end -- so ten pictures means
ten raids before you see one twice. How far through each map you are is kept in
the config file, so it survives a restart.


Using your own
--------------

Drop images into the folder named after the map:

    banners\bigmap\01 - Dorms.png
    banners\bigmap\02 - Gas station.jpg
    banners\woods\Sawmill.png
    banners\_default\anything.png

PNG and JPEG only -- anything else in the folder is ignored. Delete the pictures
that came with the mod if you would rather see only your own.

Pictures are used in file name order. A leading "01 - " or "3." only sets the
order.

_default is the fallback for any map without a folder of its own. A map with
neither keeps the game's own screen, with only the lighting following the
destination.


Size and shape
--------------

Any shape works. A picture is cropped from the centre to the shape of your screen,
never stretched, so a screenshot from a 21:9, 32:9 or 16:10 monitor can go
straight in.

The wiki pictures are 1920x1080, which is smaller than many screens. The mod scales
them up and says so in the BepInEx log:

    ...is 1920x1080 but banners show at 3440x1440 on this screen, so it will look
    soft. Save a bigger version as "01 - Dorms@large.jpg" and it will be used
    instead.

Your own screenshots, taken at your own resolution, will always be sharper than
these. That is the one good reason to replace them.


Several sizes of one picture
----------------------------

Give each size the same name, with @ and anything after it:

    01 - Dorms.png
    01 - Dorms@1440p.png
    01 - Dorms@4k.png

Those count as one picture. The mod reads each file's real size and uses the
smallest one that is still sharp on your screen, so a single folder works on any
monitor. What comes after the @ is only a label.


Updating the mod
----------------

Installing a new version never overwrites a picture that is already here, and
never deletes one. A folder you have curated stays exactly as you left it; only
maps you have nothing for receive the new art.


Location ids
------------

Folder names are matched without regard to case, so "woods" and "Woods" both work.
These are the ids as the game spells them:

    bigmap (Customs)      factory4_day        factory4_night      laboratory
    Interchange           Labyrinth           Lighthouse          RezervBase (Reserve)
    Sandbox (Ground Zero) Sandbox_high        Shoreline           Suburbs
    TarkovStreets         Terminal            Town                Woods
