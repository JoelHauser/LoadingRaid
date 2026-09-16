Deploy Screen -- where your banner images go.
=============================================

Put images in a folder named after the map:

    banners\bigmap\01 - Dorms.png
    banners\bigmap\02 - Gas station.jpg
    banners\Woods\Sawmill.png
    banners\_default\anything.png

PNG, JPG and JPEG. Anything else in the folder is ignored.

_default is the fallback: any map without a folder of its own uses it. A map with
neither its own folder nor a _default to fall back on keeps its stock images.

You do not need any images for the rest of the mod to work -- motion and map intel
apply to the stock banners too.


How many show up
----------------

The same number the map shows in vanilla -- Customs and Factory have ten, Woods
four, Labs five. Extra pictures past that are not reached; fewer pictures than that
are cycled so every slot is filled. The mod does not change the count.


Size and shape
--------------

Banners are measured on your screen. The first time you load into a raid at a
resolution, the BepInEx log says how big they really are, for example:

    [DeployScreen] banners show at 1530x920 px on this 3840x2160 screen ...

Make images at least that size and they will look sharp. The stock art is 765x460.
Any picture too small for your screen is named in the log.

Any shape works. Images are cropped from the centre to the banner's shape, never
stretched, so a screenshot from a 21:9, 32:9 or 16:10 monitor can go straight in.


Several sizes of one picture
----------------------------

Give each size the same name, with @ and anything after it:

    01 - Dorms.png
    01 - Dorms@1440p.png
    01 - Dorms@4k.png

They count as one picture. The mod reads each file's real size and uses the smallest
one that is sharp on your screen, so one folder works on any monitor. What comes
after the @ is only a label.

The first raid at a new resolution uses the largest size, because a banner can only
be measured once it is on screen. From the next raid on, the best size is used.


Ordering and captions
---------------------

Pictures are used in name order, so number them if the order matters. A leading
"01 - " or "3." is treated as ordering and dropped from the caption.

The Captions setting in F12 decides what is written over each banner:

    Map intel       (default) bosses, extracts, and your active tasks on this map
    From file name  the name before a semicolon is the heading, the rest the line under it:
                        Dorms; Three storeys, two keys.png
    Keep vanilla    the game's own headings; the file name only sets the order


Location ids
------------

Folder names are matched without regard to case, so "woods" and "Woods" both work.
These are the ids as the game spells them:

    bigmap (Customs)      factory4_day        factory4_night      laboratory
    Interchange           Labyrinth           Lighthouse          RezervBase (Reserve)
    Sandbox (Ground Zero) Sandbox_high        Shoreline           Suburbs
    TarkovStreets         Terminal            Town                Woods
