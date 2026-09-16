Deploy Screen -- where your banner images go.
=============================================

Put images in a folder named after the map:

    banners\bigmap\01 - Dorms.png
    banners\bigmap\02 - Gas station.jpg
    banners\woods\Sawmill.png
    banners\_default\anything.png

PNG, JPG and JPEG. Anything else in the folder is ignored.

_default is the fallback: any map without a folder of its own uses it. A map with
neither its own folder nor a _default to fall back on is left completely vanilla,
which is why this mod does nothing until you add something.


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

With Captions set to "From file name" in the F12 settings, the name before a pipe is
the heading and the rest is the line under it:

    Dorms|Three storey, two keys.png

With Captions set to "Keep vanilla" the file name is ignored except for ordering.


Location ids
------------

    bigmap (Customs)      factory4_day        factory4_night      interchange
    laboratory            labyrinth           lighthouse          rezervbase (Reserve)
    sandbox (Ground Zero) sandbox_high        shoreline           suburbs
    tarkovstreets         terminal            town                woods
