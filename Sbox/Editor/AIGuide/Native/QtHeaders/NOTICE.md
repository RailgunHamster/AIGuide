# Qt header overrides

These ten headers are source from Facepunch's Qt fork, commit
`fc20424f10a799a654d2eecc9539aa6fc587d046`:
https://github.com/Facepunch/qt/tree/fc20424f10a799a654d2eecc9539aa6fc587d046/valve/qtbase/src

They retain their original copyright and license notices. See `LICENSE.LGPLv3` and the notices in each header. The adapter dynamically links the Qt DLLs already distributed by s&box; it does not bundle replacement Qt DLLs. These headers override the corresponding Qt 5.15.2 SDK headers for source/binary compatibility with s&box's Facepunch/Valve Qt build. In particular, QWidget contains extra instance data, and QMainWindow contains additional virtual methods. Unmodified official QWidget headers are not ABI compatible with this engine build.

Files: QtCore/qbytearraymatcher.h, qcompilerdetection.h, qendian.h, qfloat16.h, qmutex.h, qrandom.h; QtWidgets/qdockwidget.h, qgraphicsitem.h, qmainwindow.h, qwidget.h. Their contents are supplied unchanged from the pinned source.

The rest of the headers come from the user's Qt 5.15.2 SDK at build time. Import libraries are generated from the selected engine's Qt DLL exports by Build.ps1. Preserve these source files and this notice when distributing the plugin.

The adapter's `QtAllocator.cpp` independently follows the allocator contract in the same fork's `valve/qtbase/src/valve_memoverride/valve_memoverride.cpp`: obtain matching new/delete callbacks from `tier0.dll!Qt_GetCustomAllocators`. This is required when a Qt-owned shared/weak reference is destroyed in the adapter, or a widget allocated in the adapter is deleted by Qt. Mixing the CRT allocator with Qt's allocator corrupts the heap.
