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
four, Labs five. Extra files past that are not reached; fewer files than that are
cycled so every slot is filled. The mod does not change the count.


Size
----

The stock banners are 765x460. Anything with that shape works; something far off it
will be stretched to fit.


Ordering and captions
---------------------

Files are used in name order, so number them if the order matters. A leading "01 - "
or "3." is treated as ordering and dropped from the caption.

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
