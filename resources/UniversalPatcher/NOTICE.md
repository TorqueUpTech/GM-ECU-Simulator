# UniversalPatcher XML data files

The XML files under `XML/` in this directory are copied verbatim from the
UniversalPatcher project:

  - Upstream source tree: `..\UniversalPatcher-master\XML\`
  - Project repo:         https://github.com/LarryOlson/UniversalPatcher
                          (the local fork at the path above tracks that repo)
  - Primary author:       kur4o (with contributions from Antus, NSFW, kirnbas,
                          Michael Kelly and others - see
                          `..\UniversalPatcher-master\Credits.rtf`)

## License

UniversalPatcher (and therefore these XML files) is distributed under the
**GNU General Public License v3.0**. The full text of the license is in
`LICENSE.gpl-3.0.txt` alongside this file.

The XML files describe GM PCM/ECU bin file layouts (segment offsets, part
number locations, EEPROM checkword markers, autodetect rules). They are
*data* describing how third-party firmware images are laid out, not source
code, but we treat them as covered by the upstream GPLv3 license out of
caution and respect for the author's intent.

## Why these files are vendored here

Our `Core.Identification.UpXml` code in this project reads these XML files
to identify the segment layout of a GM bin and produce a summary block of
the form:

```
BootBlock   PN: 12656811, Ver: AA, Nr: 99 [0000 - BFFF], Size: C000
OS          PN: 12656942, Ver: AA, Nr:  1 [10000 - 1BFFFF], Size: 180000
...
```

The C# code that interprets these files (`Core/Identification/UpXml/*.cs`
and its test counterparts) is an independent **clean-room** implementation
written from the XML schema only - none of UniversalPatcher's C# source
was consulted while writing it. That code is licensed alongside the rest
of this project.

## Compatibility note

Because these files are GPLv3, any redistribution of this repository in
binary or source form is bound by GPLv3 terms with respect to these
specific files. The project as a whole is currently developed in private
and not redistributed; if/when it is released, GPLv3 obligations attach
to anything that derives from these XML files (and the C# that requires
them to function).

## What was copied

- `XML/autodetect.xml`         - global platform detection rules
- `XML/e38.xml`, `e38-platform.xml`
- `XML/e67.xml`, `e67-platform.xml`
- `XML/e92.xml`                - no separate platform wrapper exists upstream
- `XML/t43.xml`, `t43-platform.xml`
- `LICENSE.gpl-3.0.txt`        - upstream LICENSE, verbatim

No source files (`.cs`, `.cpp`, etc.) from UniversalPatcher were copied.
